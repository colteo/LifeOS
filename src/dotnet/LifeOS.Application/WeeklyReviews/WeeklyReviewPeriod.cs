using LifeOS.Application.Automation;
using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.Application.WeeklyReviews;

// The local Monday–Sunday week of a review and its half-open UTC range [StartUtc, EndUtc): local
// Monday 00:00 to the next Monday 00:00, resolved with the AUTO-001 DST rules. A DST week therefore
// has 167 or 169 hours; the range is never "now - 7 days".
public sealed record WeeklyReviewPeriod(DateOnly StartDate, DateOnly EndDate, DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public static WeeklyReviewPeriod For(DateOnly weekEndDate, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (!WeeklyReview.IsWeekEnd(weekEndDate))
        {
            throw new ArgumentException("A week ends on a Sunday.", nameof(weekEndDate));
        }

        var start = WeeklyReview.WeekStartFor(weekEndDate);

        return new WeeklyReviewPeriod(
            start,
            weekEndDate,
            LocalSchedule.ToUtc(zone, start.ToDateTime(TimeOnly.MinValue)),
            LocalSchedule.ToUtc(zone, weekEndDate.AddDays(1).ToDateTime(TimeOnly.MinValue)));
    }

    public bool Contains(DateTimeOffset instant) => instant >= StartUtc && instant < EndUtc;
}
