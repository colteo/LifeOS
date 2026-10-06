using LifeOS.Application.Automation;
using LifeOS.Application.Users.SetTimeZone;

namespace LifeOS.Application.Finance.Reminders;

// AUTO-003A: when Finance reminders are due. An item scheduled on local date D is reminded at 09:00 on
// D in the user's IANA zone, with 24 h lateness: the reminder is about "today"; once the next day's
// reminder time passes the item is still shown in Transactions → Planned, but a push would be stale.
// 09:00 is after the default quiet hours (22:00–08:00); with other quiet hours the delivery waits for
// their end (NotificationDispatcher), which the 24 h delivery expiry always covers.
public static class FinanceReminderSchedule
{
    public static readonly TimeOnly Time = new(9, 0);
    public static readonly TimeSpan Lateness = TimeSpan.FromHours(24);

    public const string InvalidOccurrenceKeyCode = "InvalidOccurrenceKey";
    public const string InvalidTimeZoneCode = "InvalidTimeZone";

    // Each valid zone whose daily occurrence is open at nowUtc, with its local date and due instant.
    // Invalid stored zones schedule nothing (AUTO-001 §6).
    public static IReadOnlyDictionary<string, (FinanceReminderOpenZone Zone, DateTimeOffset DueAtUtc)> OpenZones(
        IEnumerable<string> zoneIds, DateTimeOffset nowUtc)
    {
        var open = new Dictionary<string, (FinanceReminderOpenZone, DateTimeOffset)>(StringComparer.Ordinal);

        foreach (var zoneId in zoneIds.Order(StringComparer.Ordinal))
        {
            if (TryFindZone(zoneId, out var zone) && LocalSchedule.ResolveDaily(zone, nowUtc, Time, Lateness) is { } occurrence)
            {
                open[zoneId] = (new FinanceReminderOpenZone(zoneId, occurrence.LocalDate), occurrence.DueAtUtc);
            }
        }

        return open;
    }

    // The user's local date now, in the zone the occurrence was resolved with.
    public static DateOnly LocalToday(TimeZoneInfo zone, DateTimeOffset nowUtc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).Date);

    public static bool TryFindZone(string? zoneId, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;

        if (!SetTimeZoneHandler.TryNormalizeIanaTimeZone(zoneId, out var normalized))
        {
            return false;
        }

        zone = TimeZoneInfo.FindSystemTimeZoneById(normalized);
        return true;
    }
}
