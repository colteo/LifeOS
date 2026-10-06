using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.Application.WeeklyReviews;

// Next is the last item's week-ending date; null on the last page.
public sealed record WeeklyReviewPage(IReadOnlyList<WeeklyReviewListItem> Items, DateOnly? Next);

// The user's saved weekly reviews, newest week first, one bounded page at a time (keyset paging on
// the week-ending date, unique per user). Read-only.
public sealed class GetWeeklyReviewsHandler(IWeeklyReviewRepository reviews)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    // Throws ArgumentOutOfRangeException when pageSize is not 1–MaxPageSize.
    public async Task<WeeklyReviewPage> HandleAsync(Guid userId, DateOnly? before, int pageSize, CancellationToken cancellationToken)
    {
        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException("limit", pageSize, $"The page size must be between 1 and {MaxPageSize}.");
        }

        // One row more than the page tells whether another page exists.
        var items = await reviews.GetPageAsync(userId, before, pageSize + 1, cancellationToken);

        if (items.Count <= pageSize)
        {
            return new WeeklyReviewPage(items, null);
        }

        var page = items.Take(pageSize).ToList();

        return new WeeklyReviewPage(page, page[^1].WeekEndDate);
    }
}

// One saved review exactly as generated. Null for a missing review or another user's.
public sealed class GetWeeklyReviewHandler(IWeeklyReviewRepository reviews)
{
    public Task<WeeklyReview?> HandleAsync(Guid userId, Guid reviewId, CancellationToken cancellationToken) =>
        reviews.GetAsync(userId, reviewId, cancellationToken);
}

// The module-owned "weekly review enabled" setting (default on).
public sealed class GetWeeklyReviewSettingsHandler(IWeeklyReviewRepository reviews)
{
    public Task<bool> HandleAsync(Guid userId, CancellationToken cancellationToken) =>
        reviews.IsEnabledAsync(userId, cancellationToken);
}

// Disabling stops future reviews only: saved reviews stay, and an occurrence already claimed is
// recorded as not applicable when it runs. False when the user does not exist.
public sealed class SetWeeklyReviewSettingsHandler(IWeeklyReviewRepository reviews, TimeProvider clock)
{
    public Task<bool> HandleAsync(Guid userId, bool enabled, CancellationToken cancellationToken) =>
        reviews.SetSettingsAsync(WeeklyReviewSettings.Create(userId, enabled, clock.GetUtcNow()), cancellationToken);
}
