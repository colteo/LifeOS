namespace LifeOS.Application.Automation;

public sealed record LocalOccurrence(
    DateOnly LocalDate,
    DateTimeOffset DueAtUtc,
    DateTimeOffset ExpiresAtUtc);

public static class LocalSchedule
{
    public static LocalOccurrence? ResolveWeekly(
        TimeZoneInfo zone,
        DateTimeOffset nowUtc,
        DayOfWeek day,
        TimeOnly time,
        TimeSpan maxLateness)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (maxLateness <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLateness));
        }

        var now = nowUtc.ToUniversalTime();
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var daysBack = ((int)localNow.DayOfWeek - (int)day + 7) % 7;
        var localDate = DateOnly.FromDateTime(localNow.Date).AddDays(-daysBack);
        var dueLocal = localDate.ToDateTime(time, DateTimeKind.Unspecified);
        var dueAtUtc = ToUtc(zone, dueLocal);
        var expiresAtUtc = dueAtUtc.Add(maxLateness);

        return dueAtUtc <= now && now < expiresAtUtc
            ? new LocalOccurrence(localDate, dueAtUtc, expiresAtUtc)
            : null;
    }

    // A local wall-clock time in the zone as a UTC instant, with the AUTO-001 §6 DST rules. Also used
    // for local period boundaries (e.g. a local week's midnights, AUTO-002).
    public static DateTimeOffset ToUtc(TimeZoneInfo zone, DateTime local)
    {
        ArgumentNullException.ThrowIfNull(zone);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            // AUTO-001 §6: a local time inside a spring-forward gap is shifted forward by the gap
            // length (Europe/Rome 02:30 → 03:30 CEST = 01:30Z). That is the same instant as reading
            // the local time with the offset in force just before the gap, which also holds for
            // gaps that are not one hour long.
            var beforeGap = local;

            for (var minute = 0; minute < 24 * 60 && zone.IsInvalidTime(beforeGap); minute++)
            {
                beforeGap = beforeGap.AddMinutes(-1);
            }

            if (zone.IsInvalidTime(beforeGap))
            {
                throw new InvalidTimeZoneException($"Unable to resolve invalid local time in {zone.Id}.");
            }

            return new DateTimeOffset(local, zone.GetUtcOffset(beforeGap)).ToUniversalTime();
        }

        if (zone.IsAmbiguousTime(local))
        {
            // Earlier instant = larger UTC offset (e.g. +02:00 before +01:00 in Europe/Rome).
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, offset).ToUniversalTime();
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}
