using System.Collections.Concurrent;
using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.UnitTests.Fakes;

// The AI-001 port without any AI: answers from a rule and records every snapshot it was given, so tests
// can assert exactly what would have been interpreted and how many AI calls were made.
internal sealed class FakeWeeklyReviewInterpreter : IWeeklyReviewInterpreter
{
    private readonly ConcurrentQueue<WeeklyReviewSnapshot> _inputs = new();

    public static readonly AiGenerationIdentity Identity = new("fake", "fake-model", "weekly-review-insights-v1");

    public static WeeklyReviewInsightsContent Content => new(
        "You completed 1 workout and logged meals on 1 day.",
        ["You completed 10 of 12 prescribed sets."],
        ["1 of 2 meals is not analyzed, so nutrition totals are partial."],
        [],
        ["Analyze the remaining meal so totals are complete."]);

    // Default: valid version-1 insights for any snapshot.
    public Func<WeeklyReviewSnapshot, WeeklyReviewInterpretationResult> Respond { get; set; } = _ => Success(Content);

    // Called before answering; lets a test interleave other work with an in-flight interpretation.
    public Func<WeeklyReviewSnapshot, Task>? BeforeAnswer { get; set; }

    public IReadOnlyList<WeeklyReviewSnapshot> Inputs => _inputs.ToList();

    public static WeeklyReviewInterpretationResult Success(WeeklyReviewInsightsContent content, int outputVersion = 1) =>
        WeeklyReviewInterpretationResult.Success(new WeeklyReviewInterpretation(outputVersion, content, Identity));

    public static WeeklyReviewInterpretationResult Unavailable =>
        WeeklyReviewInterpretationResult.Failed(WeeklyReviewInterpretationFailure.Unavailable);

    public static WeeklyReviewInterpretationResult InvalidOutput =>
        WeeklyReviewInterpretationResult.Failed(WeeklyReviewInterpretationFailure.InvalidOutput);

    public async Task<WeeklyReviewInterpretationResult> InterpretAsync(WeeklyReviewSnapshot snapshot, CancellationToken cancellationToken)
    {
        _inputs.Enqueue(snapshot);

        if (BeforeAnswer is { } before)
        {
            await before(snapshot);
        }

        return Respond(snapshot);
    }
}
