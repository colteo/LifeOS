using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Nutrition.Hydration;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LifeOS.IntegrationTests.PostgreSql;

// HYD-001 §8 through the real API pipeline (JWT, routing, endpoints) and real PostgreSQL. The hydration
// handlers' clock is frozen at Wednesday 14 Oct 2026 10:00 in Rome (+120); authentication keeps the
// real clock.
[Collection(PostgreSqlCollection.Name)]
public class HydrationHttpTests(PostgreSqlFixture fixture)
{
    private const string Hydration = "/api/nutrition/hydration";
    private const int Rome = 120;
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 14);

    [Fact]
    public async Task EveryEndpoint_RequiresAuthentication()
    {
        await using var factory = await FactoryAsync();
        var anonymous = factory.CreateClient();
        var intake = $"{Hydration}/intakes/{Guid.CreateVersion7()}";

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Hydration}/days/2026-10-14")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Hydration}/days?from=2026-10-08&to=2026-10-14")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{Hydration}/intakes", Add(250))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(intake, new UpdateHydrationIntakeRequest(250, new TimeOnly(8, 0)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(intake)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Hydration}/settings?date=2026-10-14")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"{Hydration}/target", new SetHydrationTargetRequest(2000, Rome))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PutAsJsonAsync($"{Hydration}/quick-add", new SetHydrationQuickAddRequest([200, 330, 750], Rome))).StatusCode);
    }

    [Fact]
    public async Task AddEditUndoAndReadBack_RoundTripThroughPostgreSql()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        var created = await client.PostAsJsonAsync($"{Hydration}/intakes", Add(250, new TimeOnly(8, 10)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var first = (await created.Content.ReadFromJsonAsync<HydrationIntakeMutationResponse>())!;
        Assert.Equal($"{Hydration}/intakes/{first.Intake.Id}", created.Headers.Location!.ToString());
        Assert.Equal((250, Today, new TimeOnly(8, 10)), (first.Intake.AmountMl, first.Intake.Date, first.Intake.Time));
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 6, 10, 0, TimeSpan.Zero), first.Intake.OccurredAtUtc);
        Assert.Equal((Now, Now), (first.Intake.CreatedAtUtc, first.Intake.UpdatedAtUtc));
        Assert.Equal(new HydrationDaySummaryResponse(Today, 250, null, false, null), first.Day);

        var second = (await (await client.PostAsJsonAsync($"{Hydration}/intakes", Add(500, new TimeOnly(10, 45))))
            .Content.ReadFromJsonAsync<HydrationIntakeMutationResponse>())!;
        Assert.Equal(750, second.Day.TotalMl);

        // Edit amount and time; the intake stays on its day.
        var edited = await client.PutAsJsonAsync($"{Hydration}/intakes/{first.Intake.Id}", new UpdateHydrationIntakeRequest(330, new TimeOnly(7, 30)));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var editedBody = (await edited.Content.ReadFromJsonAsync<HydrationIntakeMutationResponse>())!;
        Assert.Equal((330, Today, new TimeOnly(7, 30)), (editedBody.Intake.AmountMl, editedBody.Intake.Date, editedBody.Intake.Time));
        Assert.Equal(830, editedBody.Day.TotalMl);

        // Undo the last tap: 200 with the updated day.
        var undo = await client.DeleteAsync($"{Hydration}/intakes/{second.Intake.Id}");
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal(new HydrationDaySummaryResponse(Today, 330, null, false, null), await undo.Content.ReadFromJsonAsync<HydrationDaySummaryResponse>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Hydration}/intakes/{second.Intake.Id}")).StatusCode);

        // A backdated intake stays on its own day.
        await client.PostAsJsonAsync($"{Hydration}/intakes", Add(1000, new TimeOnly(12, 0), Today.AddDays(-2)));

        var day = (await client.GetFromJsonAsync<HydrationDayResponse>($"{Hydration}/days/2026-10-14"))!;
        Assert.Equal(330, day.Day.TotalMl);
        Assert.Equal([first.Intake.Id], day.Intakes.Select(intake => intake.Id));

        var range = (await client.GetFromJsonAsync<HydrationRangeResponse>($"{Hydration}/days?from=2026-10-08&to=2026-10-14"))!;
        Assert.Equal([0, 0, 0, 0, 1000, 0, 330], range.Days.Select(d => d.TotalMl));
        Assert.Equal(new HydrationRangeSummaryResponse(0, 0, 190), range.Summary);
    }

    [Fact]
    public async Task AnotherUsersIntake_IsNotFound()
    {
        await using var factory = await FactoryAsync();
        var owner = await SignInAsync(factory);
        var other = await SignInAsync(factory);

        var created = (await (await owner.PostAsJsonAsync($"{Hydration}/intakes", Add(250)))
            .Content.ReadFromJsonAsync<HydrationIntakeMutationResponse>())!;
        var path = $"{Hydration}/intakes/{created.Intake.Id}";

        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(path, new UpdateHydrationIntakeRequest(500, new TimeOnly(9, 0)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(path)).StatusCode);
        Assert.Empty((await other.GetFromJsonAsync<HydrationDayResponse>($"{Hydration}/days/2026-10-14"))!.Intakes);
        Assert.Equal(250, (await owner.GetFromJsonAsync<HydrationDayResponse>($"{Hydration}/days/2026-10-14"))!.Day.TotalMl);
    }

    [Fact]
    public async Task InvalidRequests_AreFieldKeyedProblems()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        await AssertInvalidAsync(client.PostAsJsonAsync($"{Hydration}/intakes", Add(0)), "amountMl");
        await AssertInvalidAsync(client.PostAsJsonAsync($"{Hydration}/intakes", Add(4001)), "amountMl");
        await AssertInvalidAsync(client.PostAsJsonAsync($"{Hydration}/intakes", new AddHydrationIntakeRequest(null, Today, new TimeOnly(8, 0), Rome)), "amountMl");
        await AssertInvalidAsync(client.PostAsJsonAsync($"{Hydration}/intakes", new AddHydrationIntakeRequest(250, null, new TimeOnly(8, 0), Rome)), "date");
        await AssertInvalidAsync(client.PostAsJsonAsync($"{Hydration}/intakes", new AddHydrationIntakeRequest(250, Today, null, Rome)), "time");
        await AssertInvalidAsync(client.PostAsJsonAsync($"{Hydration}/intakes", new AddHydrationIntakeRequest(250, Today, new TimeOnly(8, 0), null)), "utcOffsetMinutes");
        await AssertInvalidAsync(client.PostAsJsonAsync($"{Hydration}/intakes", new AddHydrationIntakeRequest(250, Today, new TimeOnly(8, 0), 900)), "utcOffsetMinutes");

        // Local today in Rome is 14 Oct: the 15th is a future day.
        var future = await AssertInvalidAsync(client.PostAsJsonAsync($"{Hydration}/intakes", Add(250, new TimeOnly(0, 5), Today.AddDays(1))), "date");
        Assert.Equal(HydrationLimits.FutureDayMessage, future);

        await AssertInvalidAsync(client.GetAsync($"{Hydration}/days/14-10-2026"), "date");
        await AssertInvalidAsync(client.GetAsync($"{Hydration}/days?to=2026-10-14"), "from");
        await AssertInvalidAsync(client.GetAsync($"{Hydration}/days?from=2026-10-14&to=2026-10-13"), "to");
        await AssertInvalidAsync(client.GetAsync($"{Hydration}/days?from=2026-07-01&to=2026-10-14"), "to");

        var created = (await (await client.PostAsJsonAsync($"{Hydration}/intakes", Add(250)))
            .Content.ReadFromJsonAsync<HydrationIntakeMutationResponse>())!;
        await AssertInvalidAsync(client.PutAsJsonAsync($"{Hydration}/intakes/{created.Intake.Id}", new UpdateHydrationIntakeRequest(0, new TimeOnly(8, 0))), "amountMl");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{Hydration}/intakes/{created.Intake.Id}", new UpdateHydrationIntakeRequest(250, null)), "time");

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Hydration}/intakes/{Guid.CreateVersion7()}", new UpdateHydrationIntakeRequest(250, new TimeOnly(8, 0)))).StatusCode);
    }

    [Fact]
    public async Task TargetAndQuickAdd_ApplyFromToday_AndDriveTheDays()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        var initial = (await client.GetFromJsonAsync<HydrationSettingsResponse>($"{Hydration}/settings?date=2026-10-14"))!;
        Assert.Equal((Today, (int?)null, (DateOnly?)null), (initial.Date, initial.TargetMl, initial.TargetEffectiveFrom));
        Assert.Equal([150, 250, 500], initial.QuickAddMl);

        var set = await client.PutAsJsonAsync($"{Hydration}/target", new SetHydrationTargetRequest(2000, Rome));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var target = (await set.Content.ReadFromJsonAsync<HydrationSettingsResponse>())!;
        Assert.Equal((Today, (int?)2000, (DateOnly?)Today), (target.Date, target.TargetMl, target.TargetEffectiveFrom));

        // From today only: yesterday has no target.
        var yesterday = (await client.GetFromJsonAsync<HydrationSettingsResponse>($"{Hydration}/settings?date=2026-10-13"))!;
        Assert.Null(yesterday.TargetMl);

        var added = (await (await client.PostAsJsonAsync($"{Hydration}/intakes", Add(2250)))
            .Content.ReadFromJsonAsync<HydrationIntakeMutationResponse>())!;
        Assert.Equal(new HydrationDaySummaryResponse(Today, 2250, 2000, true, 112), added.Day);

        var quickAdd = await client.PutAsJsonAsync($"{Hydration}/quick-add", new SetHydrationQuickAddRequest([200, 330, 750], Rome));
        Assert.Equal(HttpStatusCode.OK, quickAdd.StatusCode);
        Assert.Equal([200, 330, 750], (await quickAdd.Content.ReadFromJsonAsync<HydrationSettingsResponse>())!.QuickAddMl);

        var removed = (await (await client.PutAsJsonAsync($"{Hydration}/target", new SetHydrationTargetRequest(null, Rome)))
            .Content.ReadFromJsonAsync<HydrationSettingsResponse>())!;
        Assert.Equal(((int?)null, (DateOnly?)Today), (removed.TargetMl, removed.TargetEffectiveFrom));
        Assert.Equal([200, 330, 750], removed.QuickAddMl);

        var day = (await client.GetFromJsonAsync<HydrationDayResponse>($"{Hydration}/days/2026-10-14"))!;
        Assert.Equal(new HydrationDaySummaryResponse(Today, 2250, null, false, null), day.Day);

        // Another user sees none of it.
        var other = await SignInAsync(factory);
        var theirs = (await other.GetFromJsonAsync<HydrationSettingsResponse>($"{Hydration}/settings?date=2026-10-14"))!;
        Assert.Equal(((int?)null, 150), (theirs.TargetMl, theirs.QuickAddMl[0]));
    }

    [Fact]
    public async Task InvalidSettings_AreFieldKeyedProblems()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        await AssertInvalidAsync(client.GetAsync($"{Hydration}/settings"), "date");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{Hydration}/target", new SetHydrationTargetRequest(249, Rome)), "targetMl");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{Hydration}/target", new SetHydrationTargetRequest(10001, Rome)), "targetMl");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{Hydration}/target", new SetHydrationTargetRequest(2000, null)), "utcOffsetMinutes");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{Hydration}/quick-add", new SetHydrationQuickAddRequest(null, Rome)), "amountsMl");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{Hydration}/quick-add", new SetHydrationQuickAddRequest([200, 330], Rome)), "amountsMl");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{Hydration}/quick-add", new SetHydrationQuickAddRequest([200, 0, 750], Rome)), "amountsMl[1]");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{Hydration}/quick-add", new SetHydrationQuickAddRequest([200, 330, 750], null)), "utcOffsetMinutes");
    }

    // The App's client against the real API: what the Water section and the Home card call.
    [Fact]
    public async Task NutritionApiClient_RoundTripsHydration()
    {
        await using var factory = await FactoryAsync();
        var api = new LifeOS.App.Services.Nutrition.NutritionApiClient(await SignInAsync(factory));

        Assert.Equal(2000, (await api.SetHydrationTargetAsync(new SetHydrationTargetRequest(2000, Rome))).Value!.TargetMl);
        Assert.Equal([200, 330, 750], (await api.SetHydrationQuickAddAsync(new SetHydrationQuickAddRequest([200, 330, 750], Rome))).Value!.QuickAddMl);

        var added = (await api.AddHydrationAsync(Add(330))).Value!;
        Assert.Equal(new HydrationDaySummaryResponse(Today, 330, 2000, false, 16), added.Day);
        Assert.Equal(500, (await api.UpdateHydrationAsync(added.Intake.Id, new UpdateHydrationIntakeRequest(500, new TimeOnly(9, 30)))).Value!.Day.TotalMl);
        Assert.Equal([500], (await api.GetHydrationDayAsync(Today)).Value!.Intakes.Select(intake => intake.AmountMl));
        Assert.Equal(7, (await api.GetHydrationDaysAsync(Today.AddDays(-6), Today)).Value!.Days.Count);
        Assert.Equal(0, (await api.DeleteHydrationAsync(added.Intake.Id)).Value!.TotalMl);
        Assert.Equal([LifeOS.App.Services.Nutrition.NutritionApiClient.IntakeNotFoundMessage], (await api.DeleteHydrationAsync(added.Intake.Id)).Errors);
        Assert.Equal([HydrationLimits.FutureDayMessage], (await api.AddHydrationAsync(Add(250, date: Today.AddDays(1)))).Errors);
        Assert.Equal((2000, 200), ((await api.GetHydrationSettingsAsync(Today)).Value!.TargetMl, (await api.GetHydrationSettingsAsync(Today)).Value!.QuickAddMl[0]));
    }

    [Fact]
    public async Task Json_UsesCamelCaseAndIsoDates()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var response = await client.PostAsJsonAsync($"{Hydration}/intakes", Add(250, new TimeOnly(8, 10)));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal(250, root.GetProperty("intake").GetProperty("amountMl").GetInt32());
        Assert.Equal("2026-10-14", root.GetProperty("intake").GetProperty("date").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("day").GetProperty("targetMl").ValueKind);
        Assert.False(root.GetProperty("day").GetProperty("isTargetMet").GetBoolean());
    }

    // ---- Helpers ----

    private static AddHydrationIntakeRequest Add(int amount, TimeOnly? time = null, DateOnly? date = null) =>
        new(amount, date ?? Today, time ?? new TimeOnly(9, 0), Rome);

    private static async Task<string> AssertInvalidAsync(Task<HttpResponseMessage> request, string field)
    {
        using var response = await request;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = problem.RootElement.GetProperty("errors");

        Assert.Equal([field], errors.EnumerateObject().Select(property => property.Name));

        return errors.GetProperty(field)[0].GetString()!;
    }

    private async Task<HydrationApiFactory> FactoryAsync()
    {
        await using var scope = fixture.CreateScope();

        return new HydrationApiFactory(scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!);
    }

    private static async Task<HttpClient> SignInAsync(HydrationApiFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest("hydration-test-" + Guid.NewGuid(), null, null));
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);

        return client;
    }

    private sealed class HydrationApiFactory(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                // Freeze hydration's "now" without changing JWT issuance/validation clocks.
                var clock = new FixedTimeProvider(Now);
                Replace(services, provider => new AddHydrationIntakeHandler(Repository(provider), provider.GetRequiredService<HydrationDayReader>(), clock));
                Replace(services, provider => new UpdateHydrationIntakeHandler(Repository(provider), provider.GetRequiredService<HydrationDayReader>(), clock));
                Replace(services, provider => new SetHydrationTargetHandler(Repository(provider), clock));
                Replace(services, provider => new SetHydrationQuickAddHandler(Repository(provider), clock));
            });
            builder.UseSetting("ConnectionStrings:PostgreSQL", connection);
            builder.UseSetting("Authentication:LifeOS:SigningKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting(LifeOS.Api.Authentication.DevelopmentSignIn.EnabledKey, "true");
            builder.UseSetting("Authentication:Google:ClientId", "test-client-id.apps.googleusercontent.com");
            builder.UseSetting("Authentication:Google:ClientSecret", "test-client-secret");
            builder.UseSetting("Authentication:Google:AllowedEmails:0", "person@example.com");
        }

        private static IHydrationRepository Repository(IServiceProvider provider) => provider.GetRequiredService<IHydrationRepository>();

        private static void Replace<T>(IServiceCollection services, Func<IServiceProvider, T> factory) where T : class
        {
            services.RemoveAll<T>();
            services.AddScoped(factory);
        }
    }
}
