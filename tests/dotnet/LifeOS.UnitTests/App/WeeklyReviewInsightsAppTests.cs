using System.Net;
using System.Net.Http.Json;
using System.Text;
using LifeOS.App.Services.WeeklyReviews;
using LifeOS.Contracts.WeeklyReviews;

namespace LifeOS.UnitTests.App;

// AI-001 app: the AI Insights state model behind the Weekly Review detail page (checking, not
// generated, generating, available, error with retry) and its wording. Plain .NET with a stub HTTP
// handler; the Razor markup is checked by source in WeeklyReviewAppTests.
public class WeeklyReviewInsightsAppTests
{
    private static readonly Guid ReviewId = Guid.CreateVersion7();

    private static readonly WeeklyReviewInsightsResponse Insights = new(
        "A steady week.",
        ["You trained once."],
        [],
        ["Meals were logged on Monday only."],
        ["Analyze the remaining meal."],
        new DateTimeOffset(2026, 10, 5, 7, 30, 0, TimeSpan.Zero),
        1, "groq", "openai/gpt-oss-20b", "weekly-review-insights-v1");

    [Fact]
    public async Task Load_NotGenerated_ThenGenerate_IsAvailable()
    {
        var api = new ScriptedApi(
            Ok(new WeeklyReviewInsightsStateResponse(WeeklyReviewInsightsStatuses.NotGenerated, null)),
            Ok(new WeeklyReviewInsightsStateResponse(WeeklyReviewInsightsStatuses.Available, Insights)));
        var panel = new WeeklyReviewInsightsPanel(api.Client);

        Assert.Equal(WeeklyReviewInsightsState.Checking, panel.State);
        await panel.LoadAsync(ReviewId);
        Assert.Equal((WeeklyReviewInsightsState.NotGenerated, null), (panel.State, panel.Insights));

        await panel.GenerateAsync(ReviewId);

        Assert.Equal(WeeklyReviewInsightsState.Available, panel.State);
        Assert.Equal("A steady week.", panel.Insights!.Summary);
        Assert.Equal([$"GET /api/weekly-reviews/{ReviewId}/insights", $"POST /api/weekly-reviews/{ReviewId}/insights"], api.Requests);
    }

    [Fact]
    public async Task Load_OfStoredInsights_IsAvailable_WithoutGenerating()
    {
        var api = new ScriptedApi(Ok(new WeeklyReviewInsightsStateResponse(WeeklyReviewInsightsStatuses.Available, Insights)));
        var panel = new WeeklyReviewInsightsPanel(api.Client);

        await panel.LoadAsync(ReviewId);

        Assert.Equal(WeeklyReviewInsightsState.Available, panel.State);
        Assert.DoesNotContain(api.Requests, request => request.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhileGenerating_ThePanelIsBusy_AndASecondRequestIsIgnored()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>();
        var api = new ScriptedApi(Ok(new WeeklyReviewInsightsStateResponse(WeeklyReviewInsightsStatuses.NotGenerated, null)));
        var panel = new WeeklyReviewInsightsPanel(api.Client);
        await panel.LoadAsync(ReviewId);
        api.Next = release.Task;

        var generating = panel.GenerateAsync(ReviewId);
        await panel.GenerateAsync(ReviewId);

        Assert.Equal(WeeklyReviewInsightsState.Generating, panel.State);
        Assert.True(panel.IsBusy);
        Assert.Single(api.Requests, request => request.StartsWith("POST", StringComparison.Ordinal));

        release.SetResult(Ok(new WeeklyReviewInsightsStateResponse(WeeklyReviewInsightsStatuses.Available, Insights)));
        await generating;
        Assert.Equal(WeeklyReviewInsightsState.Available, panel.State);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "AI insights are unavailable right now. Try again later.")]
    [InlineData(HttpStatusCode.BadGateway, "AI insights could not be generated for this review. Try again later.")]
    public async Task AGenerationFailure_ShowsTheApiMessage_AndRetryGeneratesAgain(HttpStatusCode status, string message)
    {
        var api = new ScriptedApi(
            Ok(new WeeklyReviewInsightsStateResponse(WeeklyReviewInsightsStatuses.NotGenerated, null)),
            Problem(status, message),
            Ok(new WeeklyReviewInsightsStateResponse(WeeklyReviewInsightsStatuses.Available, Insights)));
        var panel = new WeeklyReviewInsightsPanel(api.Client);
        await panel.LoadAsync(ReviewId);

        await panel.GenerateAsync(ReviewId);

        Assert.Equal((WeeklyReviewInsightsState.Error, message, null), (panel.State, panel.ErrorMessage, panel.Insights));

        await panel.RetryAsync(ReviewId);

        Assert.Equal(WeeklyReviewInsightsState.Available, panel.State);
        Assert.Null(panel.ErrorMessage);
        Assert.Equal("POST", api.Requests[^1].Split(' ')[0]);
    }

    [Fact]
    public async Task ALoadFailure_RetryOnlyReadsAgain_NeverGenerates()
    {
        var api = new ScriptedApi(
            Problem(HttpStatusCode.InternalServerError, "Something went wrong."),
            Ok(new WeeklyReviewInsightsStateResponse(WeeklyReviewInsightsStatuses.NotGenerated, null)));
        var panel = new WeeklyReviewInsightsPanel(api.Client);

        await panel.LoadAsync(ReviewId);
        Assert.Equal(WeeklyReviewInsightsState.Error, panel.State);

        await panel.RetryAsync(ReviewId);

        Assert.Equal(WeeklyReviewInsightsState.NotGenerated, panel.State);
        Assert.All(api.Requests, request => Assert.StartsWith("GET", request));
    }

    [Fact]
    public async Task AnUnreachableApi_IsAnError_NotAnException()
    {
        var api = new ScriptedApi(Task.FromException<HttpResponseMessage>(new HttpRequestException("offline")));
        var panel = new WeeklyReviewInsightsPanel(api.Client);

        await panel.LoadAsync(ReviewId);

        Assert.Equal(WeeklyReviewInsightsState.Error, panel.State);
        Assert.False(string.IsNullOrWhiteSpace(panel.ErrorMessage));
    }

    // ---- Display ----

    [Fact]
    public void Groups_SkipEmptyLists_InAFixedOrder_AndAttributionNamesTheModelAndPrompt()
    {
        var groups = WeeklyReviewDisplay.InsightGroups(Insights);

        Assert.Equal(["Wins", "Patterns", "Focus for next week"], groups.Select(group => group.Title));
        Assert.Equal(["You trained once."], groups[0].Statements);
        Assert.Equal("groq · openai/gpt-oss-20b · weekly-review-insights-v1", WeeklyReviewDisplay.InsightsAttribution(Insights));
        Assert.Empty(WeeklyReviewDisplay.InsightGroups(Insights with { Wins = [], Patterns = [], NextWeekFocus = [] }));
    }

    [Fact]
    public void TheDisclaimer_SaysTheFiguresAreAuthoritative()
    {
        Assert.Equal("AI Insights", WeeklyReviewDisplay.InsightsTitle);
        Assert.Contains("Generated by AI", WeeklyReviewDisplay.InsightsDisclaimer);
        Assert.Contains("figures are authoritative", WeeklyReviewDisplay.InsightsDisclaimer);
        Assert.Equal(WeeklyReviewDisplay.InsightsFallbackError, WeeklyReviewDisplay.InsightsError([]));
    }

    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static HttpResponseMessage Problem(HttpStatusCode status, string detail) =>
        new(status) { Content = new StringContent($$"""{"title":"x","detail":"{{detail}}","status":{{(int)status}}}""", Encoding.UTF8, "application/problem+json") };

    // Answers requests in order; Next, when set, answers the following request instead.
    private sealed class ScriptedApi : HttpMessageHandler
    {
        private readonly Queue<Task<HttpResponseMessage>> _responses;

        public ScriptedApi(params HttpResponseMessage[] responses) : this(responses.Select(Task.FromResult).ToArray())
        {
        }

        public ScriptedApi(params Task<HttpResponseMessage>[] responses)
        {
            _responses = new Queue<Task<HttpResponseMessage>>(responses);
            Client = new WeeklyReviewsApiClient(new HttpClient(this) { BaseAddress = new Uri("https://lifeos.test/") });
        }

        public WeeklyReviewsApiClient Client { get; }

        public List<string> Requests { get; } = [];

        public Task<HttpResponseMessage>? Next { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");

            if (Next is { } next)
            {
                Next = null;
                return next;
            }

            return _responses.Dequeue();
        }
    }
}
