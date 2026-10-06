using LifeOS.Domain.Users;

namespace LifeOS.Domain.WeeklyReviews;

// AUTO-002: the saved, user-facing weekly review (PD-10: not the automation execution). One per user
// and local week (Monday–Sunday), identified by the week-ending Sunday. Immutable once created: the
// snapshot is the report, and later edits of Finance, Gym or Nutrition data never change it.
public sealed class WeeklyReview
{
    // The snapshot shape this code writes. A later shape gets a new version; stored rows keep theirs.
    public const int CurrentDataVersion = 1;

    private WeeklyReview(
        Guid id,
        Guid userId,
        DateOnly weekStartDate,
        DateOnly weekEndDate,
        string timeZoneId,
        DateTimeOffset generatedAtUtc,
        int dataVersion,
        WeeklyReviewSnapshot snapshot)
    {
        Id = id;
        UserId = userId;
        WeekStartDate = weekStartDate;
        WeekEndDate = weekEndDate;
        TimeZoneId = timeZoneId;
        GeneratedAtUtc = generatedAtUtc;
        DataVersion = dataVersion;
        Snapshot = snapshot;
    }

    public Guid Id { get; }

    public Guid UserId { get; }

    // Local Monday.
    public DateOnly WeekStartDate { get; }

    // Local Sunday; also the automation occurrence key.
    public DateOnly WeekEndDate { get; }

    // The IANA zone the week's boundaries were resolved with.
    public string TimeZoneId { get; }

    public DateTimeOffset GeneratedAtUtc { get; }

    public int DataVersion { get; }

    public WeeklyReviewSnapshot Snapshot { get; }

    public static WeeklyReview Create(
        Guid userId,
        DateOnly weekEndDate,
        string timeZoneId,
        DateTimeOffset generatedAtUtc,
        WeeklyReviewSnapshot snapshot) =>
        Restore(Guid.CreateVersion7(), userId, WeekStartFor(weekEndDate), weekEndDate, timeZoneId, generatedAtUtc, CurrentDataVersion, snapshot);

    // Rebuilds a stored review (persistence only). Applies the same invariants as Create.
    public static WeeklyReview Restore(
        Guid id,
        Guid userId,
        DateOnly weekStartDate,
        DateOnly weekEndDate,
        string timeZoneId,
        DateTimeOffset generatedAtUtc,
        int dataVersion,
        WeeklyReviewSnapshot snapshot)
    {
        if (id == Guid.Empty || userId == Guid.Empty)
        {
            throw new ArgumentException("A review id and a user id are required.");
        }

        if (!IsWeekEnd(weekEndDate) || weekStartDate != WeekStartFor(weekEndDate))
        {
            throw new ArgumentException("A weekly review covers one local Monday–Sunday week.", nameof(weekEndDate));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        if (timeZoneId.Length > User.MaxTimeZoneIdLength)
        {
            throw new ArgumentException($"Time zone id must be at most {User.MaxTimeZoneIdLength} characters.", nameof(timeZoneId));
        }

        if (dataVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(dataVersion), dataVersion, "The data version must be at least 1.");
        }

        ArgumentNullException.ThrowIfNull(snapshot);

        return new WeeklyReview(id, userId, weekStartDate, weekEndDate, timeZoneId, generatedAtUtc.ToUniversalTime(), dataVersion, snapshot);
    }

    // The week ends on Sunday (and is keyed by that date). DateOnly.MinValue/MaxValue are excluded so
    // the Monday and the next Monday always exist.
    public static bool IsWeekEnd(DateOnly date) =>
        date.DayOfWeek == DayOfWeek.Sunday && date > DateOnly.MinValue.AddDays(7) && date < DateOnly.MaxValue.AddDays(-1);

    public static DateOnly WeekStartFor(DateOnly weekEndDate) => weekEndDate.AddDays(-6);
}

// AUTO-002: the module-owned Weekly Review setting (AUTO-001 §14, approach B). One optional row per
// user; a user without a row has the default. No generic preferences framework.
public sealed class WeeklyReviewSettings
{
    public const bool DefaultEnabled = true;

    private WeeklyReviewSettings(Guid userId, bool enabled, DateTimeOffset updatedAtUtc)
    {
        UserId = userId;
        Enabled = enabled;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid UserId { get; }

    public bool Enabled { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public static WeeklyReviewSettings Create(Guid userId, bool enabled, DateTimeOffset updatedAtUtc)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        return new WeeklyReviewSettings(userId, enabled, updatedAtUtc.ToUniversalTime());
    }
}
