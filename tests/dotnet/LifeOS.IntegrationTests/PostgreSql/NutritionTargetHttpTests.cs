using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.App.Services.Nutrition;
using LifeOS.Application.Nutrition;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Nutrition;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// NUT-003 through the real API pipeline (JWT, routing, endpoints) and real PostgreSQL, with a server
// clock the test moves, so "today" and history are deterministic. The AI port is a fake that must never
// be called by target routes. Includes the app's NutritionApiClient.
[Collection(PostgreSqlCollection.Name)]
public class NutritionTargetHttpTests(PostgreSqlFixture fixture)
{
    private const string Targets = "/api/nutrition/targets";

    private static DateOnly Day(int october) => new(2026, 10, october);

    [Fact]
    public async Task EveryRoute_RequiresAuthentication()
    {
        await using var factory = await FactoryAsync();
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Targets}/current?utcOffsetMinutes=0")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Targets}?date=2026-10-04")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(Targets, Request(2200))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"{Targets}/current?utcOffsetMinutes=0")).StatusCode);
    }

    [Fact]
    public async Task NoTargetYet_IsAnEmptyState()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        var current = await CurrentAsync(client);

        Assert.Equal(Day(4), current.Date);
        Assert.Null(current.Target);
        Assert.Null((await OnAsync(client, Day(4))).Target);
    }

    [Fact]
    public async Task Set_AppliesFromTheServersLocalToday_AsManual_AndCurrentReturnsIt()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        var response = await client.PutAsJsonAsync(Targets, Request(2200, 160, 240, 70));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = (await response.Content.ReadFromJsonAsync<NutritionTargetStateResponse>())!;
        Assert.Equal(Day(4), state.Date);
        Assert.Equal(new NutritionTargetResponse(Day(4), 2200, 160, 240, 70, "Manual"), state.Target);
        Assert.Equal(state.Target, (await CurrentAsync(client)).Target);
        Assert.Null((await OnAsync(client, Day(3))).Target);
        Assert.Empty(factory.Ai.Inputs);
    }

    [Fact]
    public async Task PartialTargets_AreAccepted()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Targets, Request(null, 160))).StatusCode);

        var target = (await CurrentAsync(client)).Target!;
        Assert.Equal((null, 160m, null, null), (target.CaloriesKcal, target.ProteinGrams, target.CarbsGrams, target.FatGrams));
    }

    [Theory]
    [InlineData("""{"caloriesKcal":0,"utcOffsetMinutes":0}""", "caloriesKcal")]
    [InlineData("""{"proteinGrams":-1,"utcOffsetMinutes":0}""", "proteinGrams")]
    [InlineData("""{"caloriesKcal":10000.1,"utcOffsetMinutes":0}""", "caloriesKcal")]
    [InlineData("""{"fatGrams":1000.1,"utcOffsetMinutes":0}""", "fatGrams")]
    [InlineData("""{"utcOffsetMinutes":0}""", "target")]
    [InlineData("""{"caloriesKcal":null,"proteinGrams":null,"carbsGrams":null,"fatGrams":null,"utcOffsetMinutes":0}""", "target")]
    [InlineData("""{"caloriesKcal":2200}""", "utcOffsetMinutes")]
    [InlineData("""{"caloriesKcal":2200,"utcOffsetMinutes":900}""", "utcOffsetMinutes")]
    public async Task InvalidRequests_Are400_AndStoreNothing(string json, string field)
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        await AssertInvalidAsync(client.PutAsync(Targets, new StringContent(json, System.Text.Encoding.UTF8, "application/json")), field);

        Assert.Null((await CurrentAsync(client)).Target);
    }

    [Fact]
    public async Task InvalidQueries_Are400()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        await AssertInvalidAsync(client.GetAsync($"{Targets}?date=4-10-2026"), "date");
        await AssertInvalidAsync(client.GetAsync($"{Targets}/current"), "utcOffsetMinutes");
        await AssertInvalidAsync(client.DeleteAsync($"{Targets}/current"), "utcOffsetMinutes");
        await AssertInvalidAsync(client.DeleteAsync($"{Targets}/current?utcOffsetMinutes=-900"), "utcOffsetMinutes");
    }

    [Fact]
    public async Task AClientSuppliedEffectiveDate_IsIgnored()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        var response = await client.PutAsync(Targets, new StringContent(
            """{"caloriesKcal":2200,"utcOffsetMinutes":0,"effectiveFrom":"2026-09-01","date":"2026-09-01"}""",
            System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Day(4), (await CurrentAsync(client)).Target!.EffectiveFrom);
        Assert.Null((await OnAsync(client, Day(1))).Target);
    }

    [Fact]
    public async Task SameDayUpdate_ReplacesTodaysTarget()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        await PutAsync(client, Request(2200, 160));

        await PutAsync(client, Request(null, 170, 250));

        var target = (await CurrentAsync(client)).Target!;
        Assert.Equal((null, 170m, 250m, null), (target.CaloriesKcal, target.ProteinGrams, target.CarbsGrams, target.FatGrams));
        Assert.Equal(1, await CountAsync(client));
    }

    [Fact]
    public async Task HistoricalDates_KeepTheTargetEffectiveThen()
    {
        await using var factory = await FactoryAsync(Day(1));
        var client = await SignInAsync(factory);
        await PutAsync(client, Request(2200, 160, 240, 70));
        factory.MoveTo(Day(18));
        await PutAsync(client, Request(2400, 170, 260, 75));
        factory.MoveTo(Day(25));

        var removed = await client.DeleteAsync($"{Targets}/current?utcOffsetMinutes=0");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Null((await removed.Content.ReadFromJsonAsync<NutritionTargetStateResponse>())!.Target);
        Assert.Null((await OnAsync(client, new DateOnly(2026, 9, 30))).Target);
        Assert.Equal(2200m, (await OnAsync(client, Day(17))).Target!.CaloriesKcal);
        Assert.Equal(Day(1), (await OnAsync(client, Day(17))).Target!.EffectiveFrom);
        Assert.Equal(2400m, (await OnAsync(client, Day(24))).Target!.CaloriesKcal);
        Assert.Null((await OnAsync(client, Day(25))).Target);
        Assert.Null((await CurrentAsync(client)).Target);
        Assert.Equal(3, await CountAsync(client));
    }

    [Fact]
    public async Task Today_FollowsTheDeviceOffset()
    {
        // 22:30 UTC on 3 October: already 4 October at UTC+2.
        await using var factory = await FactoryAsync(Day(3), new TimeOnly(22, 30));
        var client = await SignInAsync(factory);

        await PutAsync(client, Request(2200) with { UtcOffsetMinutes = 120 });

        Assert.Equal(Day(4), (await OnAsync(client, Day(4))).Target!.EffectiveFrom);
        Assert.Null((await OnAsync(client, Day(3))).Target);
        Assert.Equal(Day(3), (await CurrentAsync(client, 0)).Date);
        Assert.Null((await CurrentAsync(client, 0)).Target);
        Assert.Equal(2200m, (await CurrentAsync(client, 120)).Target!.CaloriesKcal);
    }

    [Fact]
    public async Task Targets_AreIsolatedPerUser()
    {
        await using var factory = await FactoryAsync();
        var owner = await SignInAsync(factory);
        var other = await SignInAsync(factory);
        await PutAsync(owner, Request(2200));

        Assert.Null((await CurrentAsync(other)).Target);
        Assert.Null((await OnAsync(other, Day(4))).Target);
        Assert.Null((await SummaryAsync(other, Day(4))).Target);

        Assert.Equal(HttpStatusCode.OK, (await other.DeleteAsync($"{Targets}/current?utcOffsetMinutes=0")).StatusCode);
        Assert.Equal(2200m, (await CurrentAsync(owner)).Target!.CaloriesKcal);
    }

    [Fact]
    public async Task DailySummary_CarriesTheTargetOfThatDay_WithUnchangedTotals()
    {
        await using var factory = await FactoryAsync(Day(1));
        var client = await SignInAsync(factory);
        await PutAsync(client, Request(2200, null, 240));
        await CreateMealAsync(client, "Pasta", Day(2));
        factory.MoveTo(Day(5));
        await PutAsync(client, Request(2400));

        var early = await SummaryAsync(client, Day(2));
        Assert.Equal(new DailyNutritionTargetResponse(2200, null, 240, null), early.Target);
        Assert.Equal((1, 0, false, 0m), (early.MealCount, early.AnalyzedMealCount, early.AllAnalyzed, early.CaloriesKcal));
        Assert.Equal(2400m, (await SummaryAsync(client, Day(5))).Target!.CaloriesKcal);
        Assert.Null((await SummaryAsync(client, new DateOnly(2026, 9, 30))).Target);

        // Analyze day returns the same summary shape, target included; the target is not sent to AI.
        var analyzed = await client.PostAsync("/api/nutrition/analyze?date=2026-10-02", null);
        var analysis = (await analyzed.Content.ReadFromJsonAsync<NutritionAnalysisResponse>())!;
        Assert.Equal(2200m, analysis.Summary!.Target!.CaloriesKcal);
        Assert.Equal(620m, analysis.Summary.CaloriesKcal);
        Assert.Equal([new MealEstimationInput("Pasta", null)], factory.Ai.Inputs);
    }

    [Fact]
    public async Task TheAppsApiClient_SetsReadsAndRemovesTargets()
    {
        await using var factory = await FactoryAsync();
        var api = new NutritionApiClient(await SignInAsync(factory));

        Assert.Null((await api.GetCurrentTargetAsync(0)).Value!.Target);

        var set = await api.SetTargetAsync(NutritionTargetDisplay.Request(2200, null, null, 70, 0));
        Assert.True(set.IsSuccess);
        Assert.Equal(70m, set.Value!.Target!.FatGrams);
        Assert.Equal(2200m, (await api.GetCurrentTargetAsync(0)).Value!.Target!.CaloriesKcal);

        var invalid = await api.SetTargetAsync(NutritionTargetDisplay.Request(0, null, null, null, 0));
        Assert.False(invalid.IsSuccess);
        Assert.Contains("more than 0", string.Join(" ", invalid.Errors));

        Assert.True((await api.RemoveTargetAsync(0)).IsSuccess);
        Assert.Null((await api.GetCurrentTargetAsync(0)).Value!.Target);
        Assert.Empty(factory.Ai.Inputs);
    }

    private static SetNutritionTargetRequest Request(decimal? kcal, decimal? p = null, decimal? c = null, decimal? f = null) => new(kcal, p, c, f, 0);

    private static async Task PutAsync(HttpClient client, SetNutritionTargetRequest request) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Targets, request)).StatusCode);

    private static async Task<NutritionTargetStateResponse> CurrentAsync(HttpClient client, int offset = 0) =>
        (await client.GetFromJsonAsync<NutritionTargetStateResponse>($"{Targets}/current?utcOffsetMinutes={offset}"))!;

    private static async Task<NutritionTargetStateResponse> OnAsync(HttpClient client, DateOnly day) =>
        (await client.GetFromJsonAsync<NutritionTargetStateResponse>($"{Targets}?date={day:yyyy-MM-dd}"))!;

    private static async Task<DailyNutritionSummaryResponse> SummaryAsync(HttpClient client, DateOnly day) =>
        (await client.GetFromJsonAsync<DailyNutritionSummaryResponse>($"/api/nutrition/summary?date={day:yyyy-MM-dd}"))!;

    private static async Task CreateMealAsync(HttpClient client, string description, DateOnly day) =>
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/nutrition/meals",
            new CreateMealRequest(description, null, day, new TimeOnly(13, 0), 0))).StatusCode);

    private async Task<int> CountAsync(HttpClient client)
    {
        var userId = (await client.GetFromJsonAsync<LifeOS.Contracts.Users.MeResponse>("/api/me"))!.UserId;
        await using var scope = fixture.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().NutritionTargets.CountAsync(target => target.UserId == userId);
    }

    private static async Task AssertInvalidAsync(Task<HttpResponseMessage> request, string field)
    {
        using var response = await request;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", body);
        using var problem = JsonDocument.Parse(body);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), body);
    }

    // The server clock starts at 10:00 UTC on the given day (4 October by default).
    private async Task<TargetApiFactory> FactoryAsync(DateOnly? day = null, TimeOnly? time = null)
    {
        await using var scope = fixture.CreateScope();
        var clock = new ManualTimeProvider(new DateTimeOffset((day ?? Day(4)).ToDateTime(time ?? new TimeOnly(10, 0)), TimeSpan.Zero));

        return new TargetApiFactory(scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!, clock);
    }

    // Access tokens are issued with the server clock but validated against the real one, so sign-in
    // happens at the real time and the test clock is restored afterwards.
    private static async Task<HttpClient> SignInAsync(TargetApiFactory factory)
    {
        var client = factory.CreateClient();
        var testTime = factory.Clock.UtcNow;
        factory.Clock.UtcNow = DateTimeOffset.UtcNow;
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest("nutrition-target-test-" + Guid.NewGuid(), null, null));
        factory.Clock.UtcNow = testTime;
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);

        return client;
    }

    private sealed class TargetApiFactory(string connection, ManualTimeProvider clock) : WebApplicationFactory<Program>
    {
        public FakeNutritionEstimationService Ai { get; } = new();

        public ManualTimeProvider Clock => clock;

        public void MoveTo(DateOnly day) => clock.UtcNow = new DateTimeOffset(day.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:PostgreSQL", connection);
            builder.UseSetting("Authentication:LifeOS:SigningKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting(LifeOS.Api.Authentication.DevelopmentSignIn.EnabledKey, "true");
            builder.UseSetting("Authentication:Google:ClientId", "test-client-id.apps.googleusercontent.com");
            builder.UseSetting("Authentication:Google:ClientSecret", "test-client-secret");
            builder.UseSetting("Authentication:Google:AllowedEmails:0", "person@example.com");
            builder.UseSetting("NutritionAi:BaseUrl", "");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<INutritionEstimationService>(Ai);
                services.AddSingleton<TimeProvider>(clock);
            });
        }
    }
}
