using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LifeOS.App.Services.Nutrition;
using LifeOS.Application.Nutrition;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Nutrition;
using LifeOS.Domain.Nutrition;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// NUT-002 through the real API pipeline (JWT, routing, endpoints) and real PostgreSQL. The AI port is a
// fake: no Python service and no provider is ever contacted. Includes the app's NutritionApiClient.
[Collection(PostgreSqlCollection.Name)]
public class NutritionAiHttpTests(PostgreSqlFixture fixture)
{
    private const string Meals = "/api/nutrition/meals";

    // Offset 0 throughout: "today" is the UTC date of the host's real clock.
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task EveryEndpoint_RequiresAuthentication()
    {
        await using var factory = await FactoryAsync(new FakeNutritionEstimationService());
        var anonymous = factory.CreateClient();
        var meal = $"{Meals}/{Guid.CreateVersion7()}";

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/nutrition/summary?date={Today:yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"{meal}/estimate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"{meal}/nutrition", Values("AiConfirmed"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/nutrition/analyze?date={Today:yyyy-MM-dd}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/nutrition/lazy-close", new LazyCloseRequest(0))).StatusCode);
    }

    [Fact]
    public async Task Estimate_IsAProposalOnly_AndSendsOnlyTheTextAndType()
    {
        var ai = new FakeNutritionEstimationService
        {
            Respond = _ => FakeNutritionEstimationService.Estimate(620.04m, 52, 58, 20, "about 180 g chicken", "about 250 g potatoes")
        };
        await using var factory = await FactoryAsync(ai);
        var client = await SignInAsync(factory);
        var meal = await CreateAsync(client, "Pollo con le patate", Today, "Lunch");

        var response = await client.PostAsync($"{Meals}/{meal.Id}/estimate", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var estimate = (await response.Content.ReadFromJsonAsync<NutritionEstimateResponse>())!;
        Assert.Equal((620.0m, 52m, 58m, 20m), (estimate.CaloriesKcal, estimate.ProteinGrams, estimate.CarbsGrams, estimate.FatGrams));
        Assert.Equal(["about 180 g chicken", "about 250 g potatoes"], estimate.Assumptions);
        Assert.Equal([new MealEstimationInput("Pollo con le patate", MealType.Lunch)], ai.Inputs);
        Assert.Null((await DayAsync(client, Today)).Single().Nutrition);
    }

    [Fact]
    public async Task OtherUsersOrMissingMeals_Are404_WithoutAnyAiCall()
    {
        var ai = new FakeNutritionEstimationService();
        await using var factory = await FactoryAsync(ai);
        var owner = await SignInAsync(factory);
        var other = await SignInAsync(factory);
        var meal = await CreateAsync(owner, "Pasta", Today);

        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{Meals}/{meal.Id}/estimate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"{Meals}/{meal.Id}/nutrition", Values("UserAdjusted"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsync($"{Meals}/{Guid.CreateVersion7()}/estimate", null)).StatusCode);
        Assert.Equal(0, (await SummaryAsync(other, Today)).MealCount);

        Assert.Empty(ai.Inputs);
        Assert.Null((await DayAsync(owner, Today)).Single().Nutrition);
    }

    [Fact]
    public async Task ConfirmAndUpdateNutrition_PersistWithTheirSource()
    {
        await using var factory = await FactoryAsync(new FakeNutritionEstimationService());
        var client = await SignInAsync(factory);
        var meal = await CreateAsync(client, "Pasta", Today);

        var confirmed = await client.PutAsJsonAsync($"{Meals}/{meal.Id}/nutrition", Values("AiConfirmed"));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var body = (await confirmed.Content.ReadFromJsonAsync<MealResponse>())!;
        Assert.Equal(("AiConfirmed", 620m, 52.3m), (body.Nutrition!.Source, body.Nutrition.CaloriesKcal, body.Nutrition.ProteinGrams));

        var adjusted = await client.PutAsJsonAsync($"{Meals}/{meal.Id}/nutrition", new SetMealNutritionRequest(500, 40, 40, 15, "userAdjusted"));
        Assert.Equal(HttpStatusCode.OK, adjusted.StatusCode);

        var stored = (await DayAsync(client, Today)).Single().Nutrition!;
        Assert.Equal(("UserAdjusted", 500m, 40m, 40m, 15m), (stored.Source, stored.CaloriesKcal, stored.ProteinGrams, stored.CarbsGrams, stored.FatGrams));
    }

    [Fact]
    public async Task MalformedNutritionValues_Are400_WithTheField_AndChangeNothing()
    {
        await using var factory = await FactoryAsync(new FakeNutritionEstimationService());
        var client = await SignInAsync(factory);
        var meal = await CreateAsync(client, "Pasta", Today);
        var path = $"{Meals}/{meal.Id}/nutrition";

        await AssertInvalidAsync(client.PutAsJsonAsync(path, new SetMealNutritionRequest(-1, 1, 1, 1, "UserAdjusted")), "caloriesKcal");
        await AssertInvalidAsync(client.PutAsJsonAsync(path, new SetMealNutritionRequest(1, 1, 1001, 1, "UserAdjusted")), "carbsGrams");
        await AssertInvalidAsync(client.PutAsJsonAsync(path, new SetMealNutritionRequest(1, null, 1, 1, "UserAdjusted")), "proteinGrams");
        await AssertInvalidAsync(client.PutAsJsonAsync(path, new SetMealNutritionRequest(1, 1, 1, 1, "AiAutoClosed")), "source");
        await AssertInvalidAsync(client.PutAsJsonAsync(path, new SetMealNutritionRequest(1, 1, 1, 1, "1")), "source");
        await AssertInvalidAsync(client.PutAsJsonAsync(path, new SetMealNutritionRequest(1, 1, 1, 1, null)), "source");

        // Not JSON numbers: rejected by body binding (a framework 400, as for every endpoint).
        var unreadable = await client.PutAsync(path, new StringContent("""{"caloriesKcal": "lots"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, unreadable.StatusCode);

        Assert.Null((await DayAsync(client, Today)).Single().Nutrition);
    }

    [Fact]
    public async Task AnalyzeDay_PartialFailure_KeepsSuccesses_AndTotalsAreDeterministic()
    {
        var ai = new FakeNutritionEstimationService
        {
            Respond = input => input.Description == "???"
                ? FakeNutritionEstimationService.NotEstimable
                : FakeNutritionEstimationService.Estimate(740, 52, 75.5m, 24.5m)
        };
        await using var factory = await FactoryAsync(ai);
        var client = await SignInAsync(factory);
        await CreateAsync(client, "Colazione", Today, time: "08:00");
        await CreateAsync(client, "???", Today, time: "10:00");
        await CreateAsync(client, "Pranzo", Today, time: "13:00");

        var response = await client.PostAsync($"/api/nutrition/analyze?date={Today:yyyy-MM-dd}", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var analysis = (await response.Content.ReadFromJsonAsync<NutritionAnalysisResponse>())!;
        Assert.Equal((2, 1, false, true), (analysis.Analyzed, analysis.Failed, analysis.EstimationUnavailable, analysis.MorePending));
        Assert.Equal(new DailyNutritionSummaryResponse(Today, 3, 2, false, 1480m, 104m, 151m, 49m), analysis.Summary);
        Assert.Equal(analysis.Summary, await SummaryAsync(client, Today));
        Assert.All((await DayAsync(client, Today)).Where(meal => meal.Nutrition is not null), meal => Assert.Equal("AiRequested", meal.Nutrition!.Source));

        ai.Respond = _ => FakeNutritionEstimationService.Estimate(440, 34, 53, 16);
        var retry = (await (await client.PostAsync($"/api/nutrition/analyze?date={Today:yyyy-MM-dd}", null)).Content.ReadFromJsonAsync<NutritionAnalysisResponse>())!;

        Assert.Equal((1, 0, false), (retry.Analyzed, retry.Failed, retry.MorePending));
        Assert.Equal(new DailyNutritionSummaryResponse(Today, 3, 3, true, 1920m, 138m, 204m, 65m), retry.Summary);
        Assert.Equal(["Colazione", "???", "Pranzo", "???"], ai.Inputs.Select(input => input.Description));
    }

    [Fact]
    public async Task ProviderUnavailable_IsAReadable503_AndAnalyzeReportsIt()
    {
        var ai = new FakeNutritionEstimationService { Respond = _ => FakeNutritionEstimationService.Unavailable };
        await using var factory = await FactoryAsync(ai);
        var client = await SignInAsync(factory);
        var meal = await CreateAsync(client, "Pasta", Today);

        var estimate = await client.PostAsync($"{Meals}/{meal.Id}/estimate", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, estimate.StatusCode);
        var body = await estimate.Content.ReadAsStringAsync();
        Assert.Contains("Nutrition estimation is unavailable right now. Try again later.", body);
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("Groq", body, StringComparison.OrdinalIgnoreCase);

        var analysis = (await (await client.PostAsync($"/api/nutrition/analyze?date={Today:yyyy-MM-dd}", null)).Content.ReadFromJsonAsync<NutritionAnalysisResponse>())!;
        Assert.Equal((0, true, true), (analysis.Analyzed, analysis.EstimationUnavailable, analysis.MorePending));

        // The diary itself keeps working.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Meals, Create("Caffè", Today, "16:00"))).StatusCode);
    }

    [Fact]
    public async Task AnUnusableEstimate_IsAReadable422()
    {
        await using var factory = await FactoryAsync(new FakeNutritionEstimationService { Respond = _ => FakeNutritionEstimationService.NotEstimable });
        var client = await SignInAsync(factory);
        var meal = await CreateAsync(client, "asdfgh", Today);

        var response = await client.PostAsync($"{Meals}/{meal.Id}/estimate", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Could not estimate this meal.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WithoutAConfiguredAiService_EstimatesAre503_AndNothingElseBreaks()
    {
        await using var factory = await FactoryAsync(null, ("NutritionAi:BaseUrl", ""));
        var client = await SignInAsync(factory);
        var meal = await CreateAsync(client, "Pasta", Today);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync($"{Meals}/{meal.Id}/estimate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Meals}/{meal.Id}/nutrition", Values("UserAdjusted"))).StatusCode);
        Assert.Equal(1, (await SummaryAsync(client, Today)).AnalyzedMealCount);
    }

    [Fact]
    public async Task LazyClose_AnalyzesPastMealsOnly_AsAutoClosed()
    {
        var ai = new FakeNutritionEstimationService();
        await using var factory = await FactoryAsync(ai);
        var client = await SignInAsync(factory);
        await CreateAsync(client, "Oggi", Today);
        await CreateAsync(client, "Ieri", Today.AddDays(-1));

        var response = await client.PostAsJsonAsync("/api/nutrition/lazy-close", new LazyCloseRequest(0));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var analysis = (await response.Content.ReadFromJsonAsync<NutritionAnalysisResponse>())!;
        Assert.Equal((1, 0, false, false, (DailyNutritionSummaryResponse?)null),
            (analysis.Analyzed, analysis.Failed, analysis.EstimationUnavailable, analysis.MorePending, analysis.Summary));
        Assert.Null((await DayAsync(client, Today)).Single().Nutrition);
        Assert.Equal("AiAutoClosed", (await DayAsync(client, Today.AddDays(-1))).Single().Nutrition!.Source);
        Assert.Equal(["Ieri"], ai.Inputs.Select(input => input.Description));

        await AssertInvalidAsync(client.PostAsJsonAsync("/api/nutrition/lazy-close", new LazyCloseRequest(null)), "utcOffsetMinutes");
        await AssertInvalidAsync(client.PostAsJsonAsync("/api/nutrition/lazy-close", new LazyCloseRequest(900)), "utcOffsetMinutes");
    }

    [Fact]
    public async Task DescriptionChange_OnAnAnalyzedMeal_Is409_UntilConfirmed()
    {
        await using var factory = await FactoryAsync(new FakeNutritionEstimationService());
        var client = await SignInAsync(factory);
        var meal = await CreateAsync(client, "Pasta", Today, "Lunch");
        await client.PutAsJsonAsync($"{Meals}/{meal.Id}/nutrition", Values("AiConfirmed"));

        var timeOnly = await client.PutAsJsonAsync($"{Meals}/{meal.Id}", new UpdateMealRequest("Pasta", "Dinner", new TimeOnly(20, 0)));
        Assert.Equal(HttpStatusCode.OK, timeOnly.StatusCode);
        Assert.Equal("AiConfirmed", (await timeOnly.Content.ReadFromJsonAsync<MealResponse>())!.Nutrition!.Source);

        var refused = await client.PutAsJsonAsync($"{Meals}/{meal.Id}", new UpdateMealRequest("Pasta al pomodoro", "Dinner", new TimeOnly(20, 0)));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Changing the meal description will clear its nutrition analysis.", await refused.Content.ReadAsStringAsync());
        Assert.Equal("Pasta", (await DayAsync(client, Today)).Single().Description);

        var cleared = await client.PutAsJsonAsync($"{Meals}/{meal.Id}", new UpdateMealRequest("Pasta al pomodoro", "Dinner", new TimeOnly(20, 0), true));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var stored = (await DayAsync(client, Today)).Single();
        Assert.Equal(("Pasta al pomodoro", (MealNutritionResponse?)null), (stored.Description, stored.Nutrition));
        Assert.Equal(0, (await SummaryAsync(client, Today)).AnalyzedMealCount);
    }

    [Fact]
    public async Task InvalidDates_Are400()
    {
        await using var factory = await FactoryAsync(new FakeNutritionEstimationService());
        var client = await SignInAsync(factory);

        await AssertInvalidAsync(client.GetAsync("/api/nutrition/summary"), "date");
        await AssertInvalidAsync(client.GetAsync("/api/nutrition/summary?date=03/10/2026"), "date");
        await AssertInvalidAsync(client.PostAsync("/api/nutrition/analyze?date=0001-01-01", null), "date");
    }

    [Fact]
    public async Task AppClient_EstimatesConfirmsAnalyzesAndLazyCloses()
    {
        var ai = new FakeNutritionEstimationService { Respond = _ => FakeNutritionEstimationService.Estimate(620, 52, 58, 20, "about 180 g chicken") };
        await using var factory = await FactoryAsync(ai);
        var api = new NutritionApiClient(await SignInAsync(factory));
        var utc = TimeZoneInfo.Utc;
        var lunch = (await api.CreateMealAsync(MealJournal.CreateRequest("Pollo con le patate", "Lunch", Today, new TimeOnly(13, 0), utc))).Value!;
        await api.CreateMealAsync(MealJournal.CreateRequest("Caffè", "", Today, new TimeOnly(16, 0), utc));
        await api.CreateMealAsync(MealJournal.CreateRequest("Cena di ieri", "", Today.AddDays(-1), new TimeOnly(20, 0), utc));

        var proposal = (await api.EstimateAsync(lunch.Id)).Value!;
        Assert.Equal(["about 180 g chicken"], proposal.Assumptions);
        var confirmed = (await api.SetNutritionAsync(lunch.Id, NutritionDisplay.Confirm(proposal))).Value!;
        Assert.Equal(("Confirmed estimate", "620 kcal", "52 P · 58 C · 20 F"),
            (NutritionDisplay.SourceLabel(confirmed.Nutrition!.Source), NutritionDisplay.Kcal(confirmed.Nutrition), NutritionDisplay.Macros(confirmed.Nutrition)));

        var summary = (await api.GetSummaryAsync(Today)).Value!;
        Assert.Equal(("2 meals · 1 analyzed", "Analyze remaining"), (NutritionDisplay.CountLine(summary), NutritionDisplay.AnalyzeLabel(summary, isToday: true)));

        var analysis = (await api.AnalyzeDayAsync(Today)).Value!;
        Assert.Equal("2 meals · all analyzed", NutritionDisplay.CountLine(analysis.Summary!));
        Assert.Null(NutritionDisplay.AnalysisMessage(analysis));

        var closed = (await api.LazyCloseAsync(0)).Value!;
        Assert.Equal(1, closed.Analyzed);
        Assert.Equal("Auto-estimated", NutritionDisplay.SourceLabel((await api.GetMealsAsync(Today.AddDays(-1))).Value!.Single().Nutrition!.Source));

        var refused = await api.UpdateMealAsync(lunch.Id, MealJournal.UpdateRequest("Pollo e riso", "Lunch", new TimeOnly(13, 0)));
        Assert.Equal([NutritionApiClient.NutritionClearRequiredMessage], refused.Errors);

        Assert.Equal("This meal no longer exists.", (await api.EstimateAsync(Guid.CreateVersion7())).Errors.Single());
        ai.Respond = _ => FakeNutritionEstimationService.Unavailable;
        Assert.Equal("Nutrition estimation is unavailable right now. Try again later.", (await api.EstimateAsync(lunch.Id)).Errors.Single());
    }

    private static SetMealNutritionRequest Values(string source) => new(620, 52.3m, 58, 20, source);

    private static CreateMealRequest Create(string description, DateOnly day, string time = "13:00", string? mealType = null) =>
        new(description, mealType, day, TimeOnly.Parse(time), 0);

    private static async Task<MealResponse> CreateAsync(HttpClient client, string description, DateOnly day, string? mealType = null, string time = "13:00")
    {
        var response = await client.PostAsJsonAsync(Meals, Create(description, day, time, mealType));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<MealResponse>())!;
    }

    private static async Task<List<MealResponse>> DayAsync(HttpClient client, DateOnly day) =>
        (await client.GetFromJsonAsync<List<MealResponse>>($"{Meals}?date={day:yyyy-MM-dd}"))!;

    private static async Task<DailyNutritionSummaryResponse> SummaryAsync(HttpClient client, DateOnly day) =>
        (await client.GetFromJsonAsync<DailyNutritionSummaryResponse>($"/api/nutrition/summary?date={day:yyyy-MM-dd}"))!;

    private static async Task AssertInvalidAsync(Task<HttpResponseMessage> request, string field)
    {
        using var response = await request;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", body);
        using var problem = JsonDocument.Parse(body);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), body);
    }

    private async Task<NutritionAiApiFactory> FactoryAsync(FakeNutritionEstimationService? ai, params (string Key, string Value)[] settings)
    {
        await using var scope = fixture.CreateScope();

        return new NutritionAiApiFactory(scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!, ai, settings);
    }

    private static async Task<HttpClient> SignInAsync(NutritionAiApiFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest("nutrition-ai-test-" + Guid.NewGuid(), null, null));
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);

        return client;
    }

    // ai null: the real Infrastructure client, configured only by the given settings.
    private sealed class NutritionAiApiFactory(string connection, FakeNutritionEstimationService? ai, (string Key, string Value)[] settings)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:PostgreSQL", connection);
            builder.UseSetting("Authentication:LifeOS:SigningKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting(LifeOS.Api.Authentication.DevelopmentSignIn.EnabledKey, "true");
            builder.UseSetting("Authentication:Google:ClientId", "test-client-id.apps.googleusercontent.com");
            builder.UseSetting("Authentication:Google:ClientSecret", "test-client-secret");
            builder.UseSetting("Authentication:Google:AllowedEmails:0", "person@example.com");

            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            if (ai is not null)
            {
                builder.ConfigureTestServices(services => services.AddSingleton<INutritionEstimationService>(ai));
            }
        }
    }
}
