using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.Application.WeeklyReviews;

// A zone whose weekly-review occurrence is open now: its occurrence key and week-ending date.
public sealed record WeeklyReviewOpenZone(string TimeZoneId, string OccurrenceKey, DateOnly WeekEndDate);

// A user whose weekly review is due: the user's zone selects the open occurrence.
public sealed record WeeklyReviewDueUser(Guid UserId, string TimeZoneId);

// A review in the list: identity and dates only (the snapshot is read on the detail).
public sealed record WeeklyReviewListItem(Guid Id, DateOnly WeekStartDate, DateOnly WeekEndDate, DateTimeOffset GeneratedAtUtc);

// AUTO-002 persistence of weekly reviews and the module-owned enabled setting. Every user-facing read
// is scoped to userId: another user's review is indistinguishable from a missing one.
public interface IWeeklyReviewRepository
{
    // The distinct non-null users.time_zone_id values (AUTO-001 §7 zone buckets).
    Task<IReadOnlyList<string>> GetUserTimeZoneIdsAsync(CancellationToken cancellationToken);

    // At most `limit` users, by id, whose time zone is one of the open zones, who have not disabled
    // the weekly review, and who have neither an execution of automationType for that zone's
    // occurrence key nor a review for that week. Ids only (one query).
    Task<IReadOnlyList<WeeklyReviewDueUser>> FindDueUsersAsync(
        string automationType,
        IReadOnlyList<WeeklyReviewOpenZone> openZones,
        int limit,
        CancellationToken cancellationToken);

    // True without a settings row (WeeklyReviewSettings.DefaultEnabled).
    Task<bool> IsEnabledAsync(Guid userId, CancellationToken cancellationToken);

    // Creates or replaces the user's setting. False when the user does not exist.
    Task<bool> SetSettingsAsync(WeeklyReviewSettings settings, CancellationToken cancellationToken);

    Task<Guid?> FindIdAsync(Guid userId, DateOnly weekEndDate, CancellationToken cancellationToken);

    // Inserts the review unless the user already has one for that week (then false, nothing written).
    // Joins the caller's unit of work; never opens its own transaction.
    Task<bool> TryAddAsync(WeeklyReview review, CancellationToken cancellationToken);

    // At most `take` of the user's reviews, newest week first, with a week-ending date before
    // beforeWeekEndDate when given (keyset paging).
    Task<IReadOnlyList<WeeklyReviewListItem>> GetPageAsync(
        Guid userId,
        DateOnly? beforeWeekEndDate,
        int take,
        CancellationToken cancellationToken);

    Task<WeeklyReview?> GetAsync(Guid userId, Guid reviewId, CancellationToken cancellationToken);
}
