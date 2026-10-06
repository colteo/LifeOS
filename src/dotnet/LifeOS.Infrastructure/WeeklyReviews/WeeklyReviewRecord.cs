using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.Infrastructure.WeeklyReviews;

// The weekly_reviews row (AUTO-002 D-4). A persistence record rather than the Domain entity, because
// reading the snapshot document needs the row's data_version. Insert-only: rows are never updated.
internal sealed class WeeklyReviewRecord
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public DateOnly WeekStartDate { get; set; }

    public DateOnly WeekEndDate { get; set; }

    public string TimeZoneId { get; set; } = "";

    public DateTimeOffset GeneratedAtUtc { get; set; }

    public int DataVersion { get; set; }

    // jsonb document; see WeeklyReviewSnapshotJson.
    public string Snapshot { get; set; } = "";

    public static WeeklyReviewRecord From(WeeklyReview review) => new()
    {
        Id = review.Id,
        UserId = review.UserId,
        WeekStartDate = review.WeekStartDate,
        WeekEndDate = review.WeekEndDate,
        TimeZoneId = review.TimeZoneId,
        GeneratedAtUtc = review.GeneratedAtUtc,
        DataVersion = review.DataVersion,
        Snapshot = WeeklyReviewSnapshotJson.Serialize(review.DataVersion, review.Snapshot)
    };

    public WeeklyReview ToDomain() => WeeklyReview.Restore(
        Id, UserId, WeekStartDate, WeekEndDate, TimeZoneId, GeneratedAtUtc, DataVersion,
        WeeklyReviewSnapshotJson.Deserialize(DataVersion, Snapshot));
}
