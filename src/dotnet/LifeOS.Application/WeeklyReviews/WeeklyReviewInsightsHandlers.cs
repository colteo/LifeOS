using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.Application.WeeklyReviews;

public enum WeeklyReviewInsightsStatus
{
    // No such review for this user.
    NotFound,

    // The review exists; insights were never generated.
    NotGenerated,

    Available,

    // The AI service cannot answer now. Nothing was stored.
    Unavailable,

    // The AI service answered without valid insights. Nothing was stored.
    Failed
}

public sealed record WeeklyReviewInsightsResult(WeeklyReviewInsightsStatus Status, WeeklyReviewInsights? Insights = null);

// The review's insights, if any. Never calls the AI service.
public sealed class GetWeeklyReviewInsightsHandler(IWeeklyReviewRepository reviews, IWeeklyReviewInsightsRepository insights)
{
    public async Task<WeeklyReviewInsightsResult> HandleAsync(Guid userId, Guid reviewId, CancellationToken cancellationToken)
    {
        if (await insights.GetAsync(userId, reviewId, cancellationToken) is { } existing)
        {
            return new(WeeklyReviewInsightsStatus.Available, existing);
        }

        return await reviews.GetAsync(userId, reviewId, cancellationToken) is null
            ? new(WeeklyReviewInsightsStatus.NotFound)
            : new(WeeklyReviewInsightsStatus.NotGenerated);
    }
}

// AI-001: on-demand generation. Idempotent per review: existing insights are returned as they are,
// without an AI call (no duplicate, no repeated cost). Otherwise the saved snapshot, and only that, is
// interpreted; the answer is validated by the Domain and stored only if entirely valid. Any failure
// stores nothing and leaves the review untouched, so the user can simply try again.
public sealed class GenerateWeeklyReviewInsightsHandler(
    IWeeklyReviewRepository reviews,
    IWeeklyReviewInsightsRepository insights,
    IWeeklyReviewInterpreter interpreter,
    TimeProvider clock)
{
    public async Task<WeeklyReviewInsightsResult> HandleAsync(Guid userId, Guid reviewId, CancellationToken cancellationToken)
    {
        var review = await reviews.GetAsync(userId, reviewId, cancellationToken);

        if (review is null)
        {
            return new(WeeklyReviewInsightsStatus.NotFound);
        }

        if (await insights.GetAsync(userId, reviewId, cancellationToken) is { } existing)
        {
            return new(WeeklyReviewInsightsStatus.Available, existing);
        }

        var result = await interpreter.InterpretAsync(review.Snapshot, cancellationToken);

        if (result.Interpretation is not { } interpretation)
        {
            return new(result.Failure == WeeklyReviewInterpretationFailure.Unavailable
                ? WeeklyReviewInsightsStatus.Unavailable
                : WeeklyReviewInsightsStatus.Failed);
        }

        // Only the shape this code can read is ever stored.
        if (interpretation.OutputVersion != WeeklyReviewInsights.CurrentOutputVersion)
        {
            return new(WeeklyReviewInsightsStatus.Failed);
        }

        WeeklyReviewInsights created;

        try
        {
            created = WeeklyReviewInsights.Create(review.Id, interpretation.Content, interpretation.Generation, clock.GetUtcNow());
        }
        catch (ArgumentException)
        {
            return new(WeeklyReviewInsightsStatus.Failed);
        }

        if (await insights.TryAddAsync(created, cancellationToken))
        {
            return new(WeeklyReviewInsightsStatus.Available, created);
        }

        // A concurrent request stored first (its insights win), or the review was deleted meanwhile.
        return await insights.GetAsync(userId, reviewId, cancellationToken) is { } winner
            ? new(WeeklyReviewInsightsStatus.Available, winner)
            : new(WeeklyReviewInsightsStatus.NotFound);
    }
}
