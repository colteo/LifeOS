using System.Net;
using System.Text;
using System.Text.Json;
using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.WeeklyReviews;
using LifeOS.Infrastructure.WeeklyReviews;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LifeOS.UnitTests.WeeklyReviews;

// AI-001: the .NET side of the weekly-review interpretation boundary, with a scripted HTTP handler: the
// exact (minimised) payload sent, and how every service outcome maps to an application result. No
// network.
public class WeeklyReviewInterpreterClientTests
{
    // Synthetic, test-only service key (never a real secret).
    private const string ServiceKey = "test-service-key-0123456789abcdefghijklmnop";

    private static readonly WeeklyReviewSnapshot Snapshot = new(
        new WeeklyFinanceSummary([new WeeklyCurrencySummary("EUR", 42.50m, 100m, 57.50m, [new WeeklyExpenseCategory("Food", 42.50m)])]),
        new WeeklyGymSummary(1, 3600, 10, 12, [new WeeklyWorkout(new DateOnly(2026, 9, 29), "Upper", "Base", 3600, 10, 12)]),
        new WeeklyNutritionSummary(1, 2, 1, 0, 650.5m, 30m, 80m, 20m, [new WeeklyNutritionDay(new DateOnly(2026, 9, 28), 2, 1, 650.5m, 30m, 80m, 20m)]));

    private const string Insights =
        """
        {"output_version": 1, "provider": "groq", "model": "openai/gpt-oss-20b", "prompt_version": "weekly-review-insights-v1",
         "insights": {"summary": "A steady week.", "wins": ["You trained once."], "attention": [], "patterns": [], "next_week_focus": ["Analyze 1 meal."]}}
        """;

    [Fact]
    public async Task SendsOnlyTheWeeksFigures_WithWeekdaysInsteadOfDates_ToTheInterpretPath()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, Insights));

        await Client(handler).InterpretAsync(Snapshot, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://127.0.0.1:8000/v1/weekly-review/interpret", request.Uri);
        Assert.Equal([$"Bearer {ServiceKey}"], request.Authorization);
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal(["finance", "gym", "nutrition"], root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["currency", "expenses", "income", "net_flow", "expense_categories"],
            root.GetProperty("finance").GetProperty("currencies")[0].EnumerateObject().Select(property => property.Name));
        var workout = root.GetProperty("gym").GetProperty("workouts")[0];
        Assert.Equal(["day", "workout_name", "program_name", "duration_seconds", "completed_sets", "prescribed_sets"],
            workout.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Tuesday", workout.GetProperty("day").GetString());
        Assert.Equal("Monday", root.GetProperty("nutrition").GetProperty("days")[0].GetProperty("day").GetString());
        Assert.Equal(57.50m, root.GetProperty("finance").GetProperty("currencies")[0].GetProperty("net_flow").GetDecimal());
        // No identifiers, dates or zones anywhere in the payload.
        Assert.DoesNotContain("2026-", request.Body);
        Assert.DoesNotContain("Europe", request.Body);
        Assert.DoesNotContain("\"id\"", request.Body);
    }

    [Fact]
    public void ThePayload_CutsLongNamesAndBoundsLists_ButKeepsTotals()
    {
        var categories = Enumerable.Range(1, 20).Select(index => new WeeklyExpenseCategory($"Category {index}", 21 - index)).ToList();
        var workouts = Enumerable.Range(0, 30).Select(_ => new WeeklyWorkout(new DateOnly(2026, 9, 28), new string('w', 150), " Base ", 60, 1, 1)).ToList();
        var snapshot = Snapshot with
        {
            Finance = new WeeklyFinanceSummary([new WeeklyCurrencySummary("EUR", 210m, 0m, -210m, categories)]),
            Gym = new WeeklyGymSummary(30, 1800, 30, 30, workouts)
        };

        var payload = WeeklyReviewInterpreterClient.ToPayload(snapshot);

        var currency = Assert.Single(payload.Finance.Currencies);
        Assert.Equal(WeeklyReviewInterpreterClient.MaxCategories, currency.ExpenseCategories.Count);
        Assert.Equal("Category 1", currency.ExpenseCategories[0].Name);
        Assert.Equal(210m, currency.Expenses);
        Assert.Equal(WeeklyReviewInterpreterClient.MaxWorkouts, payload.Gym.Workouts.Count);
        Assert.Equal(30, payload.Gym.CompletedWorkouts);
        Assert.Equal(WeeklyReviewInterpreterClient.MaxNameLength, payload.Gym.Workouts[0].WorkoutName.Length);
        Assert.Equal("Base", payload.Gym.Workouts[0].ProgramName);
    }

    [Fact]
    public async Task Insights_AreReturnedWithTheirIdentityAndVersion()
    {
        var result = await Client(new ScriptedHandler(_ => Json(HttpStatusCode.OK, Insights))).InterpretAsync(Snapshot, CancellationToken.None);

        var interpretation = result.Interpretation!;
        Assert.Equal(1, interpretation.OutputVersion);
        Assert.Equal(new AiGenerationIdentity("groq", "openai/gpt-oss-20b", "weekly-review-insights-v1"), interpretation.Generation);
        Assert.Equal("A steady week.", interpretation.Content.Summary);
        Assert.Equal(["You trained once."], interpretation.Content.Wins);
        Assert.Equal(["Analyze 1 meal."], interpretation.Content.NextWeekFocus);
        Assert.Empty(interpretation.Content.Patterns);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, WeeklyReviewInterpretationFailure.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, WeeklyReviewInterpretationFailure.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, WeeklyReviewInterpretationFailure.Unavailable)]
    [InlineData(HttpStatusCode.Unauthorized, WeeklyReviewInterpretationFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, WeeklyReviewInterpretationFailure.InvalidOutput)]
    [InlineData(HttpStatusCode.UnprocessableEntity, WeeklyReviewInterpretationFailure.InvalidOutput)]
    public async Task ServiceErrors_MapToApplicationFailures(HttpStatusCode status, WeeklyReviewInterpretationFailure expected)
    {
        var result = await Client(new ScriptedHandler(_ => Json(status, """{"error": {"code": "x"}}"""))).InterpretAsync(Snapshot, CancellationToken.None);

        Assert.Equal(WeeklyReviewInterpretationResult.Failed(expected), result);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"output_version": 1, "provider": "groq", "model": "m", "prompt_version": "p"}""")]
    [InlineData("""{"output_version": "1", "provider": "groq", "model": "m", "prompt_version": "p", "insights": {"summary": "s"}}""")]
    [InlineData("""{"output_version": 1, "model": "m", "prompt_version": "p", "insights": {"summary": "s"}}""")]
    [InlineData("""{"output_version": 1, "provider": "groq", "model": "m", "prompt_version": "p", "insights": {"wins": []}}""")]
    public async Task MalformedAnswers_AreInvalidOutput(string body)
    {
        var result = await Client(new ScriptedHandler(_ => Json(HttpStatusCode.OK, body))).InterpretAsync(Snapshot, CancellationToken.None);

        Assert.Equal(WeeklyReviewInterpretationResult.Failed(WeeklyReviewInterpretationFailure.InvalidOutput), result);
    }

    [Fact]
    public async Task MissingLists_ArePassedOnAsMissing_SoTheDomainRejectsThem()
    {
        const string body = """{"output_version": 1, "provider": "groq", "model": "m", "prompt_version": "p", "insights": {"summary": "s", "wins": []}}""";

        var result = await Client(new ScriptedHandler(_ => Json(HttpStatusCode.OK, body))).InterpretAsync(Snapshot, CancellationToken.None);

        Assert.Null(result.Interpretation!.Content.Attention);
        Assert.Throws<ArgumentException>(() => WeeklyReviewInsights.Create(Guid.CreateVersion7(), result.Interpretation.Content,
            result.Interpretation.Generation, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task UnreachableOrTimedOut_IsUnavailable()
    {
        var refused = await Client(new ScriptedHandler(_ => throw new HttpRequestException("refused"))).InterpretAsync(Snapshot, CancellationToken.None);
        var timedOut = await Client(new ScriptedHandler(_ => throw new TaskCanceledException("timeout"))).InterpretAsync(Snapshot, CancellationToken.None);

        Assert.Equal(WeeklyReviewInterpretationFailure.Unavailable, refused.Failure);
        Assert.Equal(WeeklyReviewInterpretationFailure.Unavailable, timedOut.Failure);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Client(new ScriptedHandler(_ => Json(HttpStatusCode.OK, Insights))).InterpretAsync(Snapshot, cancellation.Token));
    }

    [Fact]
    public async Task WithoutAConfiguredService_EveryInterpretationIsUnavailable_WithoutNetwork()
    {
        var client = new WeeklyReviewInterpreterClient(null, null, NullLogger<WeeklyReviewInterpreterClient>.Instance);

        Assert.Equal(WeeklyReviewInterpretationFailure.Unavailable, (await client.InterpretAsync(Snapshot, CancellationToken.None)).Failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void AConfiguredServiceWithoutAKey_IsRejected(string? key)
    {
        using var httpClient = new HttpClient();

        Assert.Throws<ArgumentException>(() => new WeeklyReviewInterpreterClient(httpClient, key, NullLogger<WeeklyReviewInterpreterClient>.Instance));
    }

    [Fact]
    public async Task Logs_IdentityAndLatency_ButNeverTheFiguresTheInsightsOrTheKey()
    {
        var logger = new RecordingLogger();
        var success = Logged(logger, Json(HttpStatusCode.OK, Insights));
        await success.InterpretAsync(Snapshot, CancellationToken.None);
        await Logged(logger, Json(HttpStatusCode.Unauthorized, "{}")).InterpretAsync(Snapshot, CancellationToken.None);

        Assert.Contains(logger.Messages, message => message.Contains("weekly-review-insights-v1") && message.Contains("openai/gpt-oss-20b") && message.Contains(" ms"));
        Assert.Contains(logger.Messages, message => message.Contains("HTTP 401"));
        foreach (var secret in new[] { ServiceKey, "Food", "Upper", "42.5", "A steady week", "You trained once" })
        {
            Assert.DoesNotContain(logger.Messages, message => message.Contains(secret, StringComparison.Ordinal));
        }
    }

    private static WeeklyReviewInterpreterClient Client(ScriptedHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8000/") }, ServiceKey, NullLogger<WeeklyReviewInterpreterClient>.Instance);

    private static WeeklyReviewInterpreterClient Logged(RecordingLogger logger, HttpResponseMessage response) =>
        new(new HttpClient(new ScriptedHandler(_ => response)) { BaseAddress = new Uri("http://127.0.0.1:8000/") }, ServiceKey, logger);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string[] Authorization, string Body);

    private sealed class RecordingLogger : ILogger<WeeklyReviewInterpreterClient>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.Method, request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("Authorization", out var authorization) ? authorization.ToArray() : [],
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }
}
