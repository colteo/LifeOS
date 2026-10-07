using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.App.Services.WeeklyReviews;
using LifeOS.Contracts.WeeklyReviews;
using LifeOS.Domain.WeeklyReviews;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.TestHost;

namespace LifeOS.IntegrationTests.Http;

// AI-001: GET/POST /api/weekly-reviews/{id}/insights through the real API pipeline (routing, JWT,
// authorization) with in-memory persistence and a fake AI port. Includes the app's API client.
public class WeeklyReviewInsightsHttpTests
{
    private static readonly Guid UserA = Guid.CreateVersion7();
    private static readonly Guid UserB = Guid.CreateVersion7();
    private static readonly DateOnly WeekEnd = new(2026, 10, 4);

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task Insights_RequireAUserAccessToken(string method)
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA);

        var response = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), Path(review.Id)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.WeeklyReviewInterpreter.Inputs);
    }

    [Fact]
    public async Task Get_IsNotGenerated_UntilGenerated_ThenAvailable()
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA);
        var client = Client(factory, UserA);

        var before = await client.GetFromJsonAsync<WeeklyReviewInsightsStateResponse>(Path(review.Id));
        var generated = await client.PostAsync(Path(review.Id), null);
        var after = await client.GetFromJsonAsync<WeeklyReviewInsightsStateResponse>(Path(review.Id));

        Assert.Equal(new WeeklyReviewInsightsStateResponse("NotGenerated", null), before);
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        Assert.Equal("Available", after!.Status);
        var insights = after.Insights!;
        Assert.Equal(FakeWeeklyReviewInterpreter.Content.Summary, insights.Summary);
        Assert.Equal(FakeWeeklyReviewInterpreter.Content.Wins, insights.Wins);
        Assert.Equal(FakeWeeklyReviewInterpreter.Content.NextWeekFocus, insights.NextWeekFocus);
        Assert.Empty(insights.Patterns);
        Assert.Equal((1, "fake", "fake-model", "weekly-review-insights-v1"), (insights.OutputVersion, insights.Provider, insights.Model, insights.PromptVersion));
        Assert.Same(review.Snapshot, Assert.Single(factory.WeeklyReviewInterpreter.Inputs));
    }

    [Fact]
    public async Task Generate_IsIdempotent()
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA);
        var client = Client(factory, UserA);

        var first = await (await client.PostAsync(Path(review.Id), null)).Content.ReadFromJsonAsync<WeeklyReviewInsightsStateResponse>();
        var second = await (await client.PostAsync(Path(review.Id), null)).Content.ReadFromJsonAsync<WeeklyReviewInsightsStateResponse>();

        Assert.Equal(first!.Insights!.GeneratedAtUtc, second!.Insights!.GeneratedAtUtc);
        Assert.Single(factory.WeeklyReviewInterpreter.Inputs);
        Assert.Single(factory.WeeklyReviewInsights.Insights);
    }

    [Fact]
    public async Task TheResponse_HasTheDocumentedShape()
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA);

        using var body = JsonDocument.Parse(await (await Client(factory, UserA).PostAsync(Path(review.Id), null)).Content.ReadAsStringAsync());

        Assert.Equal(["status", "insights"], body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["summary", "wins", "attention", "patterns", "nextWeekFocus", "generatedAtUtc", "outputVersion", "provider", "model", "promptVersion"],
            body.RootElement.GetProperty("insights").EnumerateObject().Select(property => property.Name));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task AnotherUsersOrAMissingReview_Is404_WithoutAnAiCall(string method)
    {
        await using var factory = new LifeOSApiFactory();
        var others = AddReview(factory, UserB);
        await Client(factory, UserB).PostAsync(Path(others.Id), null);
        var client = Client(factory, UserA);

        var foreign = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), Path(others.Id)));
        var missing = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), Path(Guid.CreateVersion7())));

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.NotFound), (foreign.StatusCode, missing.StatusCode));
        var text = await foreign.Content.ReadAsStringAsync();
        Assert.DoesNotContain(FakeWeeklyReviewInterpreter.Content.Summary, text);
        Assert.Single(factory.WeeklyReviewInterpreter.Inputs);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.ServiceUnavailable, "AI insights are unavailable right now. Try again later.")]
    [InlineData(true, HttpStatusCode.BadGateway, "AI insights could not be generated for this review. Try again later.")]
    public async Task AiFailures_AreStableProblems_NothingIsStored_AndTheReviewIsUnaffected(bool invalidOutput, HttpStatusCode expected, string message)
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA);
        factory.WeeklyReviewInterpreter.Respond = _ => invalidOutput ? FakeWeeklyReviewInterpreter.InvalidOutput : FakeWeeklyReviewInterpreter.Unavailable;
        var client = Client(factory, UserA);

        var response = await client.PostAsync(Path(review.Id), null);

        Assert.Equal(expected, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(message, problem.RootElement.GetProperty("detail").GetString());
        Assert.False(problem.RootElement.TryGetProperty("exception", out _));
        Assert.Empty(factory.WeeklyReviewInsights.Insights);

        // The deterministic review is still fully readable, and insights are still "not generated".
        var detail = await client.GetFromJsonAsync<WeeklyReviewResponse>($"/api/weekly-reviews/{review.Id}");
        Assert.Equal((review.Id, 42.50m), (detail!.Id, detail.Finance.Currencies.Single().Expenses));
        Assert.Equal("NotGenerated", (await client.GetFromJsonAsync<WeeklyReviewInsightsStateResponse>(Path(review.Id)))!.Status);

        // Retry succeeds once the AI answers.
        factory.WeeklyReviewInterpreter.Respond = _ => FakeWeeklyReviewInterpreter.Success(FakeWeeklyReviewInterpreter.Content);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Path(review.Id), null)).StatusCode);
    }

    [Fact]
    public async Task WithTheRealClient_AndNoAiServiceConfigured_GenerationIsUnavailable()
    {
        await using var factory = new LifeOSApiFactory(configure: builder =>
            builder.ConfigureTestServices(services =>
            {
                // Drop the fake: the real Infrastructure client, with NutritionAi:BaseUrl empty, answers alone.
                var fake = services.Last(descriptor => descriptor.ServiceType == typeof(LifeOS.Application.WeeklyReviews.IWeeklyReviewInterpreter));
                services.Remove(fake);
            }));
        var review = AddReview(factory, UserA);

        var response = await Client(factory, UserA).PostAsync(Path(review.Id), null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(factory.WeeklyReviewInsights.Insights);
    }

    [Fact]
    public async Task AppClient_ReadsGeneratesAndReportsFailures()
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA);
        var api = new WeeklyReviewsApiClient(Client(factory, UserA));

        Assert.Equal("NotGenerated", (await api.GetInsightsAsync(review.Id)).Value!.Status);

        factory.WeeklyReviewInterpreter.Respond = _ => FakeWeeklyReviewInterpreter.Unavailable;
        var failed = await api.GenerateInsightsAsync(review.Id);
        Assert.False(failed.IsSuccess);
        Assert.Equal(["AI insights are unavailable right now. Try again later."], failed.Errors);

        factory.WeeklyReviewInterpreter.Respond = _ => FakeWeeklyReviewInterpreter.Success(FakeWeeklyReviewInterpreter.Content);
        var generated = await api.GenerateInsightsAsync(review.Id);
        Assert.Equal(FakeWeeklyReviewInterpreter.Content.Summary, generated.Value!.Insights!.Summary);
        Assert.False((await api.GetInsightsAsync(Guid.CreateVersion7())).IsSuccess);
    }

    private static string Path(Guid reviewId) => $"/api/weekly-reviews/{reviewId}/insights";

    private static HttpClient Client(LifeOSApiFactory factory, Guid userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.IssueAccessToken(userId));

        return client;
    }

    private static WeeklyReview AddReview(LifeOSApiFactory factory, Guid userId)
    {
        var review = WeeklyReview.Create(userId, WeekEnd, "Europe/Rome", new DateTimeOffset(2026, 10, 4, 18, 0, 3, TimeSpan.Zero), new WeeklyReviewSnapshot(
            new WeeklyFinanceSummary([new WeeklyCurrencySummary("EUR", 42.50m, 100m, 57.50m, [new WeeklyExpenseCategory("Food", 42.50m)])]),
            new WeeklyGymSummary(1, 3600, 10, 12, [new WeeklyWorkout(WeekEnd.AddDays(-5), "Upper", "Base", 3600, 10, 12)]),
            new WeeklyNutritionSummary(1, 2, 1, 0, 650.5m, 30m, 80m, 20m, [new WeeklyNutritionDay(WeekEnd.AddDays(-6), 2, 1, 650.5m, 30m, 80m, 20m)])));
        factory.WeeklyReviews.Reviews.Add(review);

        return review;
    }
}
