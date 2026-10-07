using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.WeeklyReviews;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.WeeklyReviews;

// AI-001 on PostgreSQL. Reads join the owning review (user-scoped). The insert is one statement,
// INSERT … ON CONFLICT (review_id) DO NOTHING: the primary key decides between concurrent
// generations, and the weekly_reviews row is never touched.
internal sealed class WeeklyReviewInsightsRepository(LifeOSDbContext db) : IWeeklyReviewInsightsRepository
{
    public async Task<WeeklyReviewInsights?> GetAsync(Guid userId, Guid reviewId, CancellationToken cancellationToken)
    {
        var row = await db.Set<WeeklyReviewInsightsRecord>()
            .AsNoTracking()
            .Where(insights => insights.ReviewId == reviewId
                && db.Set<WeeklyReviewRecord>().Any(review => review.Id == insights.ReviewId && review.UserId == userId))
            .SingleOrDefaultAsync(cancellationToken);

        return row?.ToDomain();
    }

    public async Task<bool> TryAddAsync(WeeklyReviewInsights insights, CancellationToken cancellationToken)
    {
        var row = WeeklyReviewInsightsRecord.From(insights);

        try
        {
            return await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO weekly_review_insights
                    (review_id, output_version, content, provider, model, prompt_version, generated_at_utc)
                VALUES
                    ({row.ReviewId}, {row.OutputVersion}, CAST({row.Content} AS jsonb), {row.Provider}, {row.Model},
                     {row.PromptVersion}, {row.GeneratedAtUtc})
                ON CONFLICT (review_id) DO NOTHING
                """, cancellationToken) == 1;
        }
        catch (Exception exception) when (PostgresErrors.IsForeignKeyViolation(exception, WeeklyReviewInsightsConfiguration.ReviewForeignKeyName))
        {
            // The review was deleted meanwhile.
            return false;
        }
    }
}
