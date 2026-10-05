using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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

// NUT-003 through the real API pipeline (JWT, routing, endpoints) and real PostgreSQL. Plans carry
// explicit dates, so the real clock is fine. The AI port is a fake that target routes must never call.
// Includes the app's NutritionApiClient.
[Collection(PostgreSqlCollection.Name)]
public class NutritionTargetPlanHttpTests(PostgreSqlFixture fixture)
{
    private const string Plans = "/api/nutrition/target-plans";

    private static DateOnly D(int month, int day) => new(month == 1 ? 2027 : 2026, month, day);

    [Fact]
    public async Task EveryRoute_RequiresAuthentication()
    {
        await using var factory = await FactoryAsync();
        var anonymous = factory.CreateClient();
        var id = Guid.CreateVersion7();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Plans)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Plans}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Plans, Request(D(10, 7), D(11, 3)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"{Plans}/{id}", Request(D(10, 7), D(11, 3)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"{Plans}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/nutrition/targets/resolved?date=2026-10-07")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PutAsJsonAsync("/api/nutrition/target-overrides/2026-10-07", new NutritionTargetOverrideDto("NoTarget", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/api/nutrition/target-overrides/2026-10-07")).StatusCode);
    }

    [Fact]
    public async Task CreateListAndDetail_ExposeThePeriodDefaultAndWeeklyRules()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        var response = await client.PostAsJsonAsync(Plans, Request(D(10, 7), D(11, 3), training: true));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<NutritionTargetPlanResponse>())!;
        Assert.Equal($"/api/nutrition/target-plans/{created.Id}", response.Headers.Location!.OriginalString);
        Assert.Equal((D(10, 7), D(11, 3)), (created.StartsOn, created.EndsOn));
        Assert.Equal(new NutritionTargetValuesDto(2200, 160, 240, 70), created.DefaultTarget);
        Assert.Equal(["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"], created.WeeklyRules.Select(rule => rule.Weekday));
        Assert.Equal(new NutritionTargetDayRuleDto("Monday", "Custom", new(2500, null, null, null)), created.WeeklyRules[0]);
        Assert.Equal(new NutritionTargetDayRuleDto("Tuesday", "Default", null), created.WeeklyRules[1]);
        Assert.Equal(new NutritionTargetDayRuleDto("Sunday", "NoTarget", null), created.WeeklyRules[6]);

        var detail = (await client.GetFromJsonAsync<NutritionTargetPlanResponse>($"{Plans}/{created.Id}"))!;
        Assert.Equal(created.WeeklyRules, detail.WeeklyRules);
        Assert.Equal([created.Id], (await ListAsync(client)).Select(plan => plan.Id));
        Assert.Empty(factory.Ai.Inputs);
    }

    [Fact]
    public async Task AdjacentAndMultipleFuturePeriods_AreAccepted_AndListedByStart()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        await CreateAsync(client, D(12, 2), D(1, 5));
        await CreateAsync(client, D(10, 7), D(11, 3));
        await CreateAsync(client, D(11, 4), D(12, 1));

        Assert.Equal([D(10, 7), D(11, 4), D(12, 2)], (await ListAsync(client)).Select(plan => plan.StartsOn));
    }

    [Fact]
    public async Task AnOverlap_Is409_WithAReadableMessage_AndNothingIsSaved()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        await CreateAsync(client, D(10, 7), D(11, 3));

        var response = await client.PostAsJsonAsync(Plans, Request(D(10, 28), D(11, 30)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("This period overlaps 7 Oct – 3 Nov 2026.", problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("exclusion", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nutrition_target", body);
        Assert.Single(await ListAsync(client));
    }

    [Fact]
    public async Task EditingIntoAnotherPeriod_Is409_AndEditingWithinItself_IsFine()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var a = await CreateAsync(client, D(10, 1), D(10, 31));
        await CreateAsync(client, D(11, 1), D(11, 30));

        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{Plans}/{a.Id}", Request(D(10, 1), D(11, 10)))).StatusCode);

        var edited = await client.PutAsJsonAsync($"{Plans}/{a.Id}", Request(D(10, 3), D(10, 31), training: true));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var plan = (await edited.Content.ReadFromJsonAsync<NutritionTargetPlanResponse>())!;
        Assert.Equal((a.Id, D(10, 3)), (plan.Id, plan.StartsOn));
        Assert.Equal("Custom", plan.WeeklyRules[0].Mode);
    }

    [Fact]
    public async Task DeletingAPeriod_RemovesOnlyThatPeriod()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var first = await CreateAsync(client, D(10, 7), D(11, 3));
        var second = await CreateAsync(client, D(11, 4), D(12, 1));
        await SetOverrideAsync(client, D(10, 23), "NoTarget");

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Plans}/{first.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Plans}/{first.Id}")).StatusCode);

        Assert.Equal([second.Id], (await ListAsync(client)).Select(plan => plan.Id));
        var resolved = await ResolvedAsync(client, D(10, 23));
        Assert.False(resolved.CoveredByPlan);
        Assert.Null(resolved.Override);
    }

    [Fact]
    public async Task OtherUsersPeriods_AreInvisible_AndNeverConflict()
    {
        await using var factory = await FactoryAsync();
        var owner = await SignInAsync(factory);
        var other = await SignInAsync(factory);
        var plan = await CreateAsync(owner, D(10, 7), D(11, 3));

        Assert.Empty(await ListAsync(other));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Plans}/{plan.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{Plans}/{plan.Id}", Request(D(10, 7), D(11, 3)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{Plans}/{plan.Id}")).StatusCode);
        Assert.False((await ResolvedAsync(other, D(10, 10))).CoveredByPlan);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await other.PutAsJsonAsync("/api/nutrition/target-overrides/2026-10-10", new NutritionTargetOverrideDto("NoTarget", null))).StatusCode);

        // The same period for another user is fine.
        await CreateAsync(other, D(10, 7), D(11, 3));
        Assert.Single(await ListAsync(owner));
    }

    [Theory]
    [InlineData("""{"endsOn":"2026-11-03","defaultTarget":{"caloriesKcal":2200},"weeklyRules":[]}""", "startsOn")]
    [InlineData("""{"startsOn":"2026-11-03","endsOn":"2026-10-07","defaultTarget":{"caloriesKcal":2200},"weeklyRules":RULES}""", "endsOn")]
    [InlineData("""{"startsOn":"2026-10-07","endsOn":"2026-11-03","weeklyRules":RULES}""", "weeklyRules.Monday")]
    [InlineData("""{"startsOn":"2026-10-07","endsOn":"2026-11-03","defaultTarget":{"caloriesKcal":0},"weeklyRules":RULES}""", "defaultTarget.caloriesKcal")]
    [InlineData("""{"startsOn":"2026-10-07","endsOn":"2026-11-03","defaultTarget":{"caloriesKcal":2200},"weeklyRules":[]}""", "weeklyRules")]
    [InlineData("""{"startsOn":"2026-10-07","endsOn":"2026-11-03","defaultTarget":{"caloriesKcal":2200},"weeklyRules":[{"weekday":"Funday","mode":"Default"}]}""", "weeklyRules")]
    [InlineData("""{"startsOn":"2026-10-07","endsOn":"2026-11-03","defaultTarget":{"caloriesKcal":2200},"weeklyRules":[{"weekday":"Monday","mode":"Rest"}]}""", "weeklyRules.Monday")]
    public async Task InvalidPlans_Are400_AndStoreNothing(string json, string field)
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var rules = JsonSerializer.Serialize(TargetPlanDraft.Weekdays.Select(day => new { weekday = day, mode = "Default" }));

        await AssertInvalidAsync(client.PostAsync(Plans, Json(json.Replace("RULES", rules))), field);

        Assert.Empty(await ListAsync(client));
    }

    // ---- Resolution and overrides ----

    [Fact]
    public async Task Resolved_FollowsTheWeeklyPatternAndBoundaries()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        await CreateAsync(client, D(10, 7), D(11, 3), training: true);

        Assert.Equal(2500m, (await ResolvedAsync(client, D(10, 7))).Target!.CaloriesKcal);  // Wednesday, first day
        Assert.Equal(new NutritionTargetValuesDto(2200, 160, 240, 70), (await ResolvedAsync(client, D(10, 8))).Target); // Thursday default
        var sunday = await ResolvedAsync(client, D(10, 11));
        Assert.True(sunday.CoveredByPlan);
        Assert.Null(sunday.Target);
        Assert.Equal(2200m, (await ResolvedAsync(client, D(11, 3))).Target!.CaloriesKcal); // Tuesday, last day
        Assert.False((await ResolvedAsync(client, D(11, 4))).CoveredByPlan);
        await AssertInvalidAsync(client.GetAsync("/api/nutrition/targets/resolved?date=7-10-2026"), "date");
    }

    [Fact]
    public async Task Overrides_CustomNoTargetAndRemove()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        await CreateAsync(client, D(10, 7), D(11, 3), training: true);

        var custom = await SetOverrideAsync(client, D(10, 21), "Custom", new(3000, null, null, null));
        Assert.Equal(3000m, custom.Target!.CaloriesKcal);
        Assert.Equal(new NutritionTargetOverrideDto("Custom", new(3000, null, null, null)), custom.Override);
        Assert.Equal(2500m, (await ResolvedAsync(client, D(10, 28))).Target!.CaloriesKcal);

        var none = await SetOverrideAsync(client, D(10, 21), "NoTarget");
        Assert.Null(none.Target);
        Assert.Equal("NoTarget", none.Override!.Mode);

        var removed = await client.DeleteAsync("/api/nutrition/target-overrides/2026-10-21");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var back = (await removed.Content.ReadFromJsonAsync<ResolvedNutritionTargetResponse>())!;
        Assert.Equal(2500m, back.Target!.CaloriesKcal);
        Assert.Null(back.Override);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/nutrition/target-overrides/2026-10-21")).StatusCode);
    }

    [Fact]
    public async Task OverridesOutsideEveryPeriodOrInvalid_Are400()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        await CreateAsync(client, D(10, 7), D(11, 3));

        await AssertInvalidAsync(client.PutAsJsonAsync("/api/nutrition/target-overrides/2026-11-04",
            new NutritionTargetOverrideDto("Custom", new(3000, null, null, null))), "date");
        await AssertInvalidAsync(client.PutAsJsonAsync("/api/nutrition/target-overrides/2026-10-20", new NutritionTargetOverrideDto("Default", null)), "mode");
        await AssertInvalidAsync(client.PutAsJsonAsync("/api/nutrition/target-overrides/2026-10-20", new NutritionTargetOverrideDto("Custom", null)), "target");
        await AssertInvalidAsync(client.PutAsJsonAsync("/api/nutrition/target-overrides/2026-10-20",
            new NutritionTargetOverrideDto("Custom", new(0, null, null, null))), "target.caloriesKcal");
        await AssertInvalidAsync(client.PutAsJsonAsync("/api/nutrition/target-overrides/20-10-2026", new NutritionTargetOverrideDto("NoTarget", null)), "date");
        Assert.Null((await ResolvedAsync(client, D(10, 20))).Override);
    }

    [Fact]
    public async Task TheDailySummary_UsesTheResolvedTarget_AndAnalyzeDayIsUnchanged()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        await CreateAsync(client, D(10, 7), D(11, 3), training: true);
        await SetOverrideAsync(client, D(10, 23), "Custom", new(3000, null, 250, null));
        await CreateMealAsync(client, "Pasta", D(10, 12));

        var monday = await SummaryAsync(client, D(10, 12));
        Assert.Equal(new NutritionTargetValuesDto(2500, null, null, null), monday.Target);
        Assert.Equal((1, 0, false, 0m), (monday.MealCount, monday.AnalyzedMealCount, monday.AllAnalyzed, monday.CaloriesKcal));
        Assert.Equal(new NutritionTargetValuesDto(3000, null, 250, null), (await SummaryAsync(client, D(10, 23))).Target);
        Assert.Null((await SummaryAsync(client, D(10, 11))).Target);
        Assert.Null((await SummaryAsync(client, D(11, 4))).Target);

        var analyzed = await client.PostAsync("/api/nutrition/analyze?date=2026-10-12", null);
        var analysis = (await analyzed.Content.ReadFromJsonAsync<NutritionAnalysisResponse>())!;
        Assert.Equal((1, 0), (analysis.Analyzed, analysis.Failed));
        Assert.Equal(620m, analysis.Summary!.CaloriesKcal);
        Assert.Equal(2500m, analysis.Summary.Target!.CaloriesKcal);
        Assert.Equal([new MealEstimationInput("Pasta", null)], factory.Ai.Inputs);
    }

    [Fact]
    public async Task TheAppsApiClient_PlansResolvesAndOverrides()
    {
        await using var factory = await FactoryAsync();
        var api = new NutritionApiClient(await SignInAsync(factory));
        var draft = TargetPlanDraft.New(D(10, 7));
        draft.Default.CaloriesKcal = 2200;
        draft.EndsOn = D(11, 3);

        var created = await api.CreateTargetPlanAsync(draft.ToRequest());
        Assert.True(created.IsSuccess);

        var overlapping = TargetPlanDraft.New(D(10, 28));
        overlapping.Default.CaloriesKcal = 2400;
        var conflict = await api.CreateTargetPlanAsync(overlapping.ToRequest());
        Assert.Equal(["This period overlaps 7 Oct – 3 Nov 2026."], conflict.Errors);

        var edit = TargetPlanDraft.From(created.Value!);
        edit.Days[0].Mode = "Custom";
        edit.Days[0].Target.CaloriesKcal = 2500;
        Assert.True((await api.UpdateTargetPlanAsync(created.Value!.Id, edit.ToRequest())).IsSuccess);
        Assert.Equal("Custom", (await api.GetTargetPlansAsync()).Value!.Single().WeeklyRules[0].Mode);

        Assert.Equal(2500m, (await api.GetResolvedTargetAsync(D(10, 12))).Value!.Target!.CaloriesKcal);
        Assert.Null((await api.SetTargetOverrideAsync(D(10, 12), new("NoTarget", null))).Value!.Target);
        Assert.Equal(2500m, (await api.RemoveTargetOverrideAsync(D(10, 12))).Value!.Target!.CaloriesKcal);
        Assert.Equal([SetNutritionTargetOverrideHandler.OutsidePlanMessage],
            (await api.SetTargetOverrideAsync(D(12, 1), new("NoTarget", null))).Errors);

        Assert.True((await api.DeleteTargetPlanAsync(created.Value.Id)).IsSuccess);
        Assert.Equal([NutritionApiClient.PlanNotFoundMessage], (await api.DeleteTargetPlanAsync(created.Value.Id)).Errors);
        Assert.Empty(factory.Ai.Inputs);
    }

    // ---- Helpers ----

    private static NutritionTargetPlanRequest Request(DateOnly startsOn, DateOnly endsOn, bool training = false) => new(startsOn, endsOn,
        new(2200, 160, 240, 70),
        TargetPlanDraft.Weekdays.Select(day => (training, day) switch
        {
            (true, "Monday" or "Wednesday" or "Friday") => new NutritionTargetDayRuleDto(day, "Custom", new(2500, null, null, null)),
            (true, "Sunday") => new NutritionTargetDayRuleDto(day, "NoTarget", null),
            _ => new NutritionTargetDayRuleDto(day, "Default", null)
        }).ToList());

    private static async Task<NutritionTargetPlanResponse> CreateAsync(HttpClient client, DateOnly startsOn, DateOnly endsOn, bool training = false)
    {
        var response = await client.PostAsJsonAsync(Plans, Request(startsOn, endsOn, training));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NutritionTargetPlanResponse>())!;
    }

    private static async Task<List<NutritionTargetPlanResponse>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<NutritionTargetPlanResponse>>(Plans))!;

    private static async Task<ResolvedNutritionTargetResponse> ResolvedAsync(HttpClient client, DateOnly day) =>
        (await client.GetFromJsonAsync<ResolvedNutritionTargetResponse>($"/api/nutrition/targets/resolved?date={day:yyyy-MM-dd}"))!;

    private static async Task<ResolvedNutritionTargetResponse> SetOverrideAsync(HttpClient client, DateOnly day, string mode,
        NutritionTargetValuesDto? target = null)
    {
        var response = await client.PutAsJsonAsync($"/api/nutrition/target-overrides/{day:yyyy-MM-dd}", new NutritionTargetOverrideDto(mode, target));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ResolvedNutritionTargetResponse>())!;
    }

    private static async Task<DailyNutritionSummaryResponse> SummaryAsync(HttpClient client, DateOnly day) =>
        (await client.GetFromJsonAsync<DailyNutritionSummaryResponse>($"/api/nutrition/summary?date={day:yyyy-MM-dd}"))!;

    private static async Task CreateMealAsync(HttpClient client, string description, DateOnly day) =>
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/nutrition/meals",
            new CreateMealRequest(description, null, day, new TimeOnly(13, 0), 0))).StatusCode);

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task AssertInvalidAsync(Task<HttpResponseMessage> request, string field)
    {
        using var response = await request;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", body);
        using var problem = JsonDocument.Parse(body);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), body);
    }

    private async Task<PlanApiFactory> FactoryAsync()
    {
        await using var scope = fixture.CreateScope();
        return new PlanApiFactory(scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!);
    }

    private static async Task<HttpClient> SignInAsync(PlanApiFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest("nutrition-plan-test-" + Guid.NewGuid(), null, null));
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);

        return client;
    }

    private sealed class PlanApiFactory(string connection) : WebApplicationFactory<Program>
    {
        public FakeNutritionEstimationService Ai { get; } = new();

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
            builder.ConfigureTestServices(services => services.AddSingleton<INutritionEstimationService>(Ai));
        }
    }
}
