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
        var dueAtUtc = ResolveLocalToUtc(zone, dueLocal);
        var expiresAtUtc = dueAtUtc.Add(maxLateness);

        return dueAtUtc <= now && now < expiresAtUtc
            ? new LocalOccurrence(localDate, dueAtUtc, expiresAtUtc)
            : null;
    }

    private static DateTimeOffset ResolveLocalToUtc(TimeZoneInfo zone, DateTime local)
    {
        if (zone.IsInvalidTime(local))
        {
            // Shift forward to the first valid local minute. This implements the documented
            // "shift by the gap" rule without assuming a one-hour DST transition.
            var shifted = local;

            for (var minute = 0; minute < 24 * 60 && zone.IsInvalidTime(shifted); minute++)
            {
                shifted = shifted.AddMinutes(1);
            }

            if (zone.IsInvalidTime(shifted))
            {
                throw new InvalidTimeZoneException($"Unable to resolve invalid local time in {zone.Id}.");
            }

            local = shifted;
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
