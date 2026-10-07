using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.UnitTests.Fakes;

// Same rules as the PostgreSQL repository: at most one insights entry per review, never replaced; reads
// scoped to the review's owner; nothing stored for a review that does not exist. The SQL itself is
// proven against PostgreSQL in the integration tests.
internal sealed class InMemoryWeeklyReviewInsightsRepository(InMemoryWeeklyReviewRepository reviews) : IWeeklyReviewInsightsRepository
{
    private readonly Lock _lock = new();

    public Dictionary<Guid, WeeklyReviewInsights> Insights { get; } = [];

    public int AddAttempts { get; private set; }

    public Task<WeeklyReviewInsights?> GetAsync(Guid userId, Guid reviewId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var owned = reviews.Reviews.Any(review => review.Id == reviewId && review.UserId == userId);

            return Task.FromResult(owned && Insights.TryGetValue(reviewId, out var insights) ? insights : null);
        }
    }

    public Task<bool> TryAddAsync(WeeklyReviewInsights insights, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            AddAttempts++;

            if (!reviews.Reviews.Any(review => review.Id == insights.ReviewId))
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(Insights.TryAdd(insights.ReviewId, insights));
        }
    }
}
