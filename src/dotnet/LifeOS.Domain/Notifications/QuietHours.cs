namespace LifeOS.Domain.Notifications;

// AUTO-003A: the user's quiet hours, a range of local wall-clock times during which reminder
// notifications are not sent. Start is inclusive, End exclusive, minute precision. Start > End crosses
// midnight (the default 22:00–08:00). Start = End is rejected: it would mean "always" or "never".
//
// Times are local to the user's IANA zone, never UTC. NextAllowedUtc is DST-safe:
// - an End inside a spring-forward gap is reached when the gap ends (the wall clock jumps past it);
// - an End that occurs twice (fall-back) is the first occurrence after the instant, so a wall clock
//   that falls back into the range keeps it quiet until End occurs again.
public sealed record QuietHours
{
    public static readonly TimeOnly DefaultStart = new(22, 0);
    public static readonly TimeOnly DefaultEnd = new(8, 0);

    public static QuietHours Default { get; } = new(DefaultStart, DefaultEnd);

    private QuietHours(TimeOnly start, TimeOnly end)
    {
        Start = start;
        End = end;
    }

    public TimeOnly Start { get; }

    public TimeOnly End { get; }

    public bool CrossesMidnight => Start > End;

    public static QuietHours Create(TimeOnly start, TimeOnly end)
    {
        if (!IsWholeMinute(start) || !IsWholeMinute(end))
        {
            throw new ArgumentException("Quiet hours use whole minutes.");
        }

        if (start == end)
        {
            throw new ArgumentException("Quiet hours must start and end at different times.");
        }

        return new QuietHours(start, end);
    }

    public static bool IsValid(TimeOnly start, TimeOnly end) => IsWholeMinute(start) && IsWholeMinute(end) && start != end;

    // Whether a local wall-clock time is quiet. [Start, End), wrapping at midnight when Start > End.
    public bool Contains(TimeOnly localTime) =>
        CrossesMidnight
            ? localTime >= Start || localTime < End
            : localTime >= Start && localTime < End;

    // The first instant at or after instantUtc at which a notification may be sent in this zone:
    // instantUtc itself when outside quiet hours, otherwise the end of the current quiet period.
    public DateTimeOffset NextAllowedUtc(TimeZoneInfo zone, DateTimeOffset instantUtc)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var instant = instantUtc.ToUniversalTime();
        var local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
        var localTime = TimeOnly.FromDateTime(local);

        if (!Contains(localTime))
        {
            return instant;
        }

        // The quiet period ends today, unless it crosses midnight and started today.
        var endDate = DateOnly.FromDateTime(local);

        if (CrossesMidnight && localTime >= Start)
        {
            endDate = endDate.AddDays(1);
        }

        return FirstInstantAtOrAfter(zone, endDate.ToDateTime(End, DateTimeKind.Unspecified), instant);
    }

    // The first instant after `after` at which the zone's wall clock shows localEnd (or has jumped past
    // it in a gap).
    private static DateTimeOffset FirstInstantAtOrAfter(TimeZoneInfo zone, DateTime localEnd, DateTimeOffset after)
    {
        if (zone.IsInvalidTime(localEnd))
        {
            // Spring-forward gap: the first valid local time after it is when the clock passes End.
            var firstValid = localEnd;

            for (var minute = 0; minute < 24 * 60 && zone.IsInvalidTime(firstValid); minute++)
            {
                firstValid = firstValid.AddMinutes(1);
            }

            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(firstValid, zone), TimeSpan.Zero);
        }

        if (zone.IsAmbiguousTime(localEnd))
        {
            // Fall-back: End occurs twice; take the earliest occurrence still ahead of the instant.
            var candidates = zone.GetAmbiguousTimeOffsets(localEnd)
                .Select(offset => new DateTimeOffset(localEnd, offset).ToUniversalTime())
                .Where(candidate => candidate > after)
                .Order()
                .ToList();

            if (candidates.Count > 0)
            {
                return candidates[0];
            }
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, zone), TimeSpan.Zero);
    }

    private static bool IsWholeMinute(TimeOnly time) => time.Second == 0 && time.Millisecond == 0 && time.Microsecond == 0 && time.Nanosecond == 0;
}
