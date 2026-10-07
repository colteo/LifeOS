using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.Application.WeeklyReviews;

// AI-001: the AI capability that interprets ONE saved weekly review. Implemented in Infrastructure by a
// client of the Python AI service; no provider, framework or transport is visible here.
//
// Input is exactly the immutable snapshot: the interpreter never queries Finance, Gym or Nutrition,
// never sees user ids, review ids, dates or zones, and cannot change anything in LifeOS.
public interface IWeeklyReviewInterpreter
{
    // Never throws for expected failures (unreachable, timeout, unusable output): those are results.
    Task<WeeklyReviewInterpretationResult> InterpretAsync(WeeklyReviewSnapshot snapshot, CancellationToken cancellationToken);
}

// The interpreter's raw answer; Application validates it into WeeklyReviewInsights before any use.
public sealed record WeeklyReviewInterpretation(int OutputVersion, WeeklyReviewInsightsContent Content, AiGenerationIdentity Generation);

public enum WeeklyReviewInterpretationFailure
{
    // Not configured, unreachable, timed out or rate-limited: try again later.
    Unavailable,

    // The interpreter answered without valid insights (rejected, malformed or out of bounds).
    InvalidOutput
}

public sealed record WeeklyReviewInterpretationResult(WeeklyReviewInterpretation? Interpretation, WeeklyReviewInterpretationFailure? Failure)
{
    public static WeeklyReviewInterpretationResult Success(WeeklyReviewInterpretation interpretation) => new(interpretation, null);

    public static WeeklyReviewInterpretationResult Failed(WeeklyReviewInterpretationFailure failure) => new(null, failure);
}

// AI-001 persistence of insights, one row per review. Every read is scoped to the review's owner:
// another user's insights are indistinguishable from missing ones.
public interface IWeeklyReviewInsightsRepository
{
    Task<WeeklyReviewInsights?> GetAsync(Guid userId, Guid reviewId, CancellationToken cancellationToken);

    // Inserts unless the review already has insights or no longer exists (then false, nothing
    // written). Never replaces existing insights.
    Task<bool> TryAddAsync(WeeklyReviewInsights insights, CancellationToken cancellationToken);
}
