using System.Globalization;

namespace LifeOS.Application.Reminders;

// One occurrence of a reminder: a scheduled local wall-clock time, or a snooze instant.
public sealed record ReminderOccurrence(Guid ReminderId, DateTime? Local, DateTimeOffset? SnoozeUtc)
{
    public static ReminderOccurrence Scheduled(Guid reminderId, DateTime local) => new(reminderId, local, null);

    public static ReminderOccurrence Snooze(Guid reminderId, DateTimeOffset snoozeUtc) => new(reminderId, null, snoozeUtc);
}

// REM-001 D-5: the occurrence key, derived from local calendar data (AUTO-001 §8), so concurrent ticks
// and retries claim the same row and a zone change cannot produce a second key for one wall-clock time:
//   scheduled  "<reminder id>:2026-10-20T10:00"      (53 characters)
//   snooze     "<reminder id>:s:2026-10-20T08:15Z"   (56 characters)
// Only [A-Za-z0-9:-] (the stable-code rule), at most 64 characters.
public static class ReminderOccurrenceKey
{
    private const string LocalFormat = "yyyy-MM-dd'T'HH:mm";
    private const string SnoozeFormat = "yyyy-MM-dd'T'HH:mm'Z'";
    private const string SnoozeMarker = ":s:";

    public static string Format(ReminderOccurrence occurrence) =>
        occurrence.SnoozeUtc is { } snooze
            ? string.Create(CultureInfo.InvariantCulture, $"{occurrence.ReminderId:D}{SnoozeMarker}{snooze.UtcDateTime.ToString(SnoozeFormat, CultureInfo.InvariantCulture)}")
            : string.Create(CultureInfo.InvariantCulture, $"{occurrence.ReminderId:D}:{occurrence.Local!.Value.ToString(LocalFormat, CultureInfo.InvariantCulture)}");

    public static bool TryParse(string? key, out ReminderOccurrence occurrence)
    {
        occurrence = null!;

        if (key is not { Length: > 37 } || key[36] != ':' || !Guid.TryParseExact(key[..36], "D", out var reminderId) || reminderId == Guid.Empty)
        {
            return false;
        }

        if (key.Length == 56 && string.CompareOrdinal(key, 36, SnoozeMarker, 0, SnoozeMarker.Length) == 0)
        {
            if (!DateTime.TryParseExact(key[39..], SnoozeFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var snooze))
            {
                return false;
            }

            occurrence = ReminderOccurrence.Snooze(reminderId, new DateTimeOffset(snooze, TimeSpan.Zero));
            return true;
        }

        if (key.Length != 53 || !DateTime.TryParseExact(key[37..], LocalFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return false;
        }

        occurrence = ReminderOccurrence.Scheduled(reminderId, DateTime.SpecifyKind(local, DateTimeKind.Unspecified));
        return true;
    }
}
