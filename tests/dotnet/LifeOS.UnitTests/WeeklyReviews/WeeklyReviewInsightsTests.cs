using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.WeeklyReviews;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.WeeklyReviews;

// AI-001: the WeeklyReviewInsights invariants (bounded, versioned, attributable) and the on-demand
// generation use case: idempotent per review, user-scoped, nothing stored on any failure, and the
// deterministic review never changed.
public class WeeklyReviewInsightsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 30, 0, TimeSpan.FromHours(2));

    private static readonly WeeklyReviewSnapshot Snapshot = new(
        new WeeklyFinanceSummary([new WeeklyCurrencySummary("EUR", 42.50m, 0m, -42.50m, [new WeeklyExpenseCategory("Food", 42.50m)])]),
        new WeeklyGymSummary(1, 3600, 10, 12, [new WeeklyWorkout(new DateOnly(2026, 9, 29), "Upper", "Base", 3600, 10, 12)]),
        new WeeklyNutritionSummary(1, 2, 1, 0, 650.5m, 30m, 80m, 20m, [new WeeklyNutritionDay(new DateOnly(2026, 9, 28), 2, 1, 650.5m, 30m, 80m, 20m)]));

    private readonly InMemoryWeeklyReviewRepository _reviews = new();
    private readonly FakeWeeklyReviewInterpreter _ai = new();
    private readonly InMemoryWeeklyReviewInsightsRepository _insights;

    public WeeklyReviewInsightsTests()
    {
        _insights = new InMemoryWeeklyReviewInsightsRepository(_reviews);
    }

    // ---- Domain ----

    [Fact]
    public void Create_TrimsAndKeepsTheContent_AtTheCurrentOutputVersion_InUtc()
    {
        var content = new WeeklyReviewInsightsContent("  A quiet week. ", [" Win. "], [], [], []);

        var insights = WeeklyReviewInsights.Create(Guid.CreateVersion7(), content, FakeWeeklyReviewInterpreter.Identity, Now);

        Assert.Equal((1, "A quiet week.", "Win."), (insights.OutputVersion, insights.Content.Summary, insights.Content.Wins.Single()));
        Assert.Equal(FakeWeeklyReviewInterpreter.Identity, insights.Generation);
        Assert.Equal(TimeSpan.Zero, insights.GeneratedAtUtc.Offset);
        Assert.Equal(Now, insights.GeneratedAtUtc);
    }

    [Fact]
    public void Create_AcceptsTheLimits_AndEmptyLists()
    {
        var limits = new WeeklyReviewInsightsContent(
            new string('s', WeeklyReviewInsights.MaxSummaryLength),
            Enumerable.Repeat(new string('w', WeeklyReviewInsights.MaxStatementLength), WeeklyReviewInsights.MaxStatementsPerGroup).ToList(),
            [], [], []);

        var insights = WeeklyReviewInsights.Create(Guid.CreateVersion7(), limits, FakeWeeklyReviewInterpreter.Identity, Now);

        Assert.Equal(3, insights.Content.Wins.Count);
        Assert.Empty(insights.Content.Patterns);
    }

    public static TheoryData<WeeklyReviewInsightsContent> InvalidContent => new()
    {
        new("", [], [], [], []),
        new("   ", [], [], [], []),
        new(new string('s', 401), [], [], [], []),
        new("Two\nlines.", [], [], [], []),
        new("Ok.", ["a", "b", "c", "d"], [], [], []),
        new("Ok.", [], [new string('x', 201)], [], []),
        new("Ok.", [], [], [""], []),
        new("Ok.", [], [], [], [null!]),
        new("Ok.", null!, [], [], []),
        new(null!, [], [], [], [])
    };

    [Theory]
    [MemberData(nameof(InvalidContent))]
    public void Create_RejectsAnythingOutsideTheContract(WeeklyReviewInsightsContent content)
    {
        Assert.Throws<ArgumentException>(() => WeeklyReviewInsights.Create(Guid.CreateVersion7(), content, FakeWeeklyReviewInterpreter.Identity, Now));
    }

    [Theory]
    [InlineData("", "m", "p")]
    [InlineData("groq", " ", "p")]
    [InlineData("groq", "m", null)]
    public void Create_RequiresTheGenerationIdentity(string provider, string model, string? prompt)
    {
        Assert.Throws<ArgumentException>(() => WeeklyReviewInsights.Create(Guid.CreateVersion7(), FakeWeeklyReviewInterpreter.Content,
            new AiGenerationIdentity(provider, model, prompt!), Now));
    }

    [Fact]
    public void Restore_RejectsAnInvalidVersionOrReview()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WeeklyReviewInsights.Restore(Guid.CreateVersion7(), 0, FakeWeeklyReviewInterpreter.Content,
            FakeWeeklyReviewInterpreter.Identity, Now));
        Assert.Throws<ArgumentException>(() => WeeklyReviewInsights.Create(Guid.Empty, FakeWeeklyReviewInterpreter.Content,
            FakeWeeklyReviewInterpreter.Identity, Now));
    }

    // ---- Generate ----

    [Fact]
    public async Task Generate_InterpretsExactlyTheSavedSnapshot_AndStoresAttributedInsights()
    {
        var review = AddReview(TestUsers.A);

        var result = await Generate(TestUsers.A, review.Id);

        Assert.Equal(WeeklyReviewInsightsStatus.Available, result.Status);
        Assert.Same(review.Snapshot, Assert.Single(_ai.Inputs));
        var stored = _insights.Insights[review.Id];
        Assert.Same(stored, result.Insights);
        Assert.Equal((review.Id, 1, FakeWeeklyReviewInterpreter.Identity, Now), (stored.ReviewId, stored.OutputVersion, stored.Generation, stored.GeneratedAtUtc));
        Assert.Equal(FakeWeeklyReviewInterpreter.Content.Summary, stored.Content.Summary);
    }

    [Fact]
    public async Task Generate_IsIdempotent_ASecondRequestReturnsTheStoredInsightsWithoutAnAiCall()
    {
        var review = AddReview(TestUsers.A);
        var first = await Generate(TestUsers.A, review.Id);
        _ai.Respond = _ => FakeWeeklyReviewInterpreter.Success(FakeWeeklyReviewInterpreter.Content with { Summary = "Different." });

        var second = await Generate(TestUsers.A, review.Id);

        Assert.Equal(WeeklyReviewInsightsStatus.Available, second.Status);
        Assert.Same(first.Insights, second.Insights);
        Assert.Single(_ai.Inputs);
        Assert.Single(_insights.Insights);
    }

    [Fact]
    public async Task Generate_ConcurrentRequests_KeepOneLogicalInsights_TheFirstStoredWins()
    {
        var review = AddReview(TestUsers.A);
        var winner = WeeklyReviewInsights.Create(review.Id, FakeWeeklyReviewInterpreter.Content with { Summary = "Stored first." },
            FakeWeeklyReviewInterpreter.Identity, Now);
        // Another request stores its insights while this one waits for the AI.
        _ai.BeforeAnswer = _ => _insights.TryAddAsync(winner, CancellationToken.None);

        var result = await Generate(TestUsers.A, review.Id);

        Assert.Equal(WeeklyReviewInsightsStatus.Available, result.Status);
        Assert.Same(winner, result.Insights);
        Assert.Same(winner, Assert.Single(_insights.Insights).Value);
        Assert.Equal(2, _insights.AddAttempts);
    }

    [Theory]
    [InlineData(WeeklyReviewInterpretationFailure.Unavailable, WeeklyReviewInsightsStatus.Unavailable)]
    [InlineData(WeeklyReviewInterpretationFailure.InvalidOutput, WeeklyReviewInsightsStatus.Failed)]
    public async Task Generate_AFailure_StoresNothing_AndLeavesTheReviewUntouched_SoItCanBeRetried(
        WeeklyReviewInterpretationFailure failure, WeeklyReviewInsightsStatus expected)
    {
        var review = AddReview(TestUsers.A);
        _ai.Respond = _ => WeeklyReviewInterpretationResult.Failed(failure);

        var result = await Generate(TestUsers.A, review.Id);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Insights);
        Assert.Empty(_insights.Insights);
        Assert.Equal(0, _insights.AddAttempts);
        Assert.Same(review, Assert.Single(_reviews.Reviews));
        Assert.Same(Snapshot, review.Snapshot);

        _ai.Respond = _ => FakeWeeklyReviewInterpreter.Success(FakeWeeklyReviewInterpreter.Content);
        Assert.Equal(WeeklyReviewInsightsStatus.Available, (await Generate(TestUsers.A, review.Id)).Status);
    }

    public static TheoryData<WeeklyReviewInsightsContent> OutOfContract => new()
    {
        FakeWeeklyReviewInterpreter.Content with { Wins = ["a", "b", "c", "d"] },
        FakeWeeklyReviewInterpreter.Content with { Summary = new string('x', 401) },
        FakeWeeklyReviewInterpreter.Content with { Patterns = null! }
    };

    [Theory]
    [MemberData(nameof(OutOfContract))]
    public async Task Generate_AnAnswerOutsideTheContract_IsFailed_AndNothingPartialIsStored(WeeklyReviewInsightsContent content)
    {
        var review = AddReview(TestUsers.A);
        _ai.Respond = _ => FakeWeeklyReviewInterpreter.Success(content);

        var result = await Generate(TestUsers.A, review.Id);

        Assert.Equal(WeeklyReviewInsightsStatus.Failed, result.Status);
        Assert.Empty(_insights.Insights);
    }

    [Fact]
    public async Task Generate_AnUnknownOutputVersion_IsFailed()
    {
        var review = AddReview(TestUsers.A);
        _ai.Respond = _ => FakeWeeklyReviewInterpreter.Success(FakeWeeklyReviewInterpreter.Content, outputVersion: 2);

        Assert.Equal(WeeklyReviewInsightsStatus.Failed, (await Generate(TestUsers.A, review.Id)).Status);
        Assert.Empty(_insights.Insights);
    }

    [Fact]
    public async Task Generate_AnotherUsersOrAMissingReview_IsNotFound_WithoutAnAiCall()
    {
        var others = AddReview(TestUsers.B);

        Assert.Equal(WeeklyReviewInsightsStatus.NotFound, (await Generate(TestUsers.A, others.Id)).Status);
        Assert.Equal(WeeklyReviewInsightsStatus.NotFound, (await Generate(TestUsers.A, Guid.CreateVersion7())).Status);
        Assert.Empty(_ai.Inputs);
        Assert.Empty(_insights.Insights);
    }

    // ---- Get ----

    [Fact]
    public async Task Get_IsNotGenerated_ThenAvailable_AndNeverCallsTheAi()
    {
        var review = AddReview(TestUsers.A);
        var handler = new GetWeeklyReviewInsightsHandler(_reviews, _insights);

        var before = await handler.HandleAsync(TestUsers.A, review.Id, CancellationToken.None);
        await Generate(TestUsers.A, review.Id);
        var after = await handler.HandleAsync(TestUsers.A, review.Id, CancellationToken.None);

        Assert.Equal((WeeklyReviewInsightsStatus.NotGenerated, null), (before.Status, before.Insights));
        Assert.Equal(WeeklyReviewInsightsStatus.Available, after.Status);
        Assert.Single(_ai.Inputs);
    }

    [Fact]
    public async Task Get_AnotherUsersInsights_AreNotFound()
    {
        var others = AddReview(TestUsers.B);
        await Generate(TestUsers.B, others.Id);

        var result = await new GetWeeklyReviewInsightsHandler(_reviews, _insights).HandleAsync(TestUsers.A, others.Id, CancellationToken.None);

        Assert.Equal((WeeklyReviewInsightsStatus.NotFound, null), (result.Status, result.Insights));
    }

    private Task<WeeklyReviewInsightsResult> Generate(Guid userId, Guid reviewId) =>
        new GenerateWeeklyReviewInsightsHandler(_reviews, _insights, _ai, new FixedTimeProvider(Now)).HandleAsync(userId, reviewId, CancellationToken.None);

    private WeeklyReview AddReview(Guid userId)
    {
        var review = WeeklyReview.Create(userId, new DateOnly(2026, 10, 4), "Europe/Rome", Now.AddHours(-13), Snapshot);
        _reviews.Reviews.Add(review);

        return review;
    }
}
