using LifeOS.Application.Automation;
using LifeOS.Application.Notifications;
using LifeOS.Application.Users.SetTimeZone;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Reminders;

namespace LifeOS.Application.Reminders;

// REM-001 §6 (R3): the user's own reminders on the AUTO-001 engine. No scheduler of its own: the
// external tick runs discovery (FindDueAsync), the core claims each occurrence by insert on
// (user, "Reminder", occurrence key) and runs ExecuteAsync; the notification goes through the outbox,
// waits for the end of quiet hours (UserReminder is a reminder type) and is retried per device.
//
// Discovery joins each reminder's local next_due_local with its owner's zone's local "now" (D-3), so a
// zone change needs no rewrite. Of the occurrences missed since next_due_local only the latest is
// delivered; missed ones beyond the 6 h lateness are skipped as Missed, never replayed (D-14).
//
// The push carries fixed copy and the reminder's id only (D-8): the title and note stay in the app.
// After every executed occurrence the reminder moves on inside the completion transaction (D-7).
public sealed class ReminderAutomationHandler(
    IReminderScheduleStore store,
    INotificationPreferencesRepository preferences,
    TimeProvider clock) : IAutomationHandler
{
    public const string Type = "Reminder";
    public const string ResourceType = "reminder";

    public const string InvalidOccurrenceKeyCode = "InvalidOccurrenceKey";
    public const string InvalidTimeZoneCode = "InvalidTimeZone";

    // D-4: a personal reminder is still worth sending up to 6 h late.
    public static readonly TimeSpan Lateness = TimeSpan.FromHours(6);

    public string AutomationType => Type;

    public TimeSpan MaxLateness => Lateness;

    public async Task<IReadOnlyList<DueOccurrence>> FindDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return [];
        }

        var now = nowUtc.ToUniversalTime();
        var zones = new Dictionary<string, (TimeZoneInfo Zone, DateTime LocalNow)>(StringComparer.Ordinal);

        // Invalid stored zones schedule nothing (AUTO-001 §6).
        foreach (var zoneId in (await store.GetUserTimeZoneIdsAsync(cancellationToken)).Order(StringComparer.Ordinal))
        {
            if (TryFindZone(zoneId, out var zone))
            {
                zones[zoneId] = (zone, ReminderSchedule.ToMinute(TimeZoneInfo.ConvertTime(now, zone).DateTime));
            }
        }

        if (zones.Count == 0)
        {
            return [];
        }

        var candidates = await store.FindCandidatesAsync(
            zones.Select(entry => new ZoneLocalNow(entry.Key, entry.Value.LocalNow)).ToList(), now, limit * 2, cancellationToken);

        var due = new List<(DueOccurrence Occurrence, ReminderCandidate Candidate, ReminderOccurrence Of)>();
        var missed = new List<MissedOccurrence>();
        var missedSnoozes = new List<MissedSnooze>();

        foreach (var candidate in candidates)
        {
            if (!zones.TryGetValue(candidate.TimeZoneId, out var zone))
            {
                continue;
            }

            if (candidate.SnoozeUntilUtc is { } snooze && snooze <= now)
            {
                if (now >= snooze + Lateness)
                {
                    missedSnoozes.Add(new MissedSnooze(candidate.UserId, candidate.ReminderId, snooze));
                }
                else
                {
                    var of = ReminderOccurrence.Snooze(candidate.ReminderId, snooze);
                    due.Add((new DueOccurrence(candidate.UserId, ReminderOccurrenceKey.Format(of), candidate.TimeZoneId, snooze), candidate, of));
                }
            }

            if (candidate.Status == ReminderStatus.Active && candidate.NextDueLocal is { } next && next <= zone.LocalNow)
            {
                // The latest occurrence up to now; earlier missed ones are not replayed.
                var local = candidate.Schedule.LatestAtOrBefore(zone.LocalNow) is { } latest && latest >= next ? latest : next;
                var dueAtUtc = LocalSchedule.ToUtc(zone.Zone, local);

                if (dueAtUtc > now)
                {
                    // Inside a spring-forward gap: due when the gap ends.
                    continue;
                }

                if (now >= dueAtUtc + Lateness)
                {
                    missed.Add(new MissedOccurrence(candidate.UserId, candidate.ReminderId, next, candidate.Schedule.FirstAfter(local)));
                }
                else
                {
                    var of = ReminderOccurrence.Scheduled(candidate.ReminderId, local);
                    due.Add((new DueOccurrence(candidate.UserId, ReminderOccurrenceKey.Format(of), candidate.TimeZoneId, dueAtUtc), candidate, of));
                }
            }
        }

        // An occurrence that already ran to a terminal state (e.g. failed for good) is moved past; one that
        // is still running or waiting for a retry belongs to the core's retry phase.
        var existing = due.Count == 0
            ? new Dictionary<string, bool>()
            : await store.GetExecutionsAsync(Type, due.Select(item => (item.Occurrence.UserId, item.Occurrence.OccurrenceKey)).ToList(), cancellationToken);
        var result = new List<DueOccurrence>();

        foreach (var (occurrence, candidate, of) in due)
        {
            if (!existing.TryGetValue(occurrence.OccurrenceKey, out var terminal))
            {
                result.Add(occurrence);
            }
            else if (terminal && of.SnoozeUtc is { } snooze)
            {
                missedSnoozes.Add(new MissedSnooze(candidate.UserId, candidate.ReminderId, snooze));
            }
            else if (terminal && of.Local is { } local)
            {
                missed.Add(new MissedOccurrence(candidate.UserId, candidate.ReminderId, candidate.NextDueLocal!.Value, candidate.Schedule.FirstAfter(local)));
            }
        }

        if (missed.Count > 0 || missedSnoozes.Count > 0)
        {
            await store.SkipMissedAsync(missed, missedSnoozes, now, cancellationToken);
        }

        return result.Take(limit).ToList();
    }

    public async Task<AutomationResult> ExecuteAsync(AutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        if (!ReminderOccurrenceKey.TryParse(occurrence.OccurrenceKey, out var of))
        {
            return AutomationResult.PermanentFailure(InvalidOccurrenceKeyCode);
        }

        if (!TryFindZone(occurrence.TimeZoneId, out _))
        {
            return AutomationResult.PermanentFailure(InvalidTimeZoneCode);
        }

        // Re-read now: deleted, paused, edited or snoozed again since discovery means this occurrence no
        // longer exists.
        var reminder = await store.FindAsync(occurrence.UserId, of.ReminderId, cancellationToken);

        if (reminder is null || reminder.Status == ReminderStatus.Paused)
        {
            return AutomationResult.NotApplicable();
        }

        if (of.Local is { } local && (reminder.Status != ReminderStatus.Active || !reminder.Schedule.Includes(local)))
        {
            return AutomationResult.NotApplicable();
        }

        if (of.SnoozeUtc is { } snooze && reminder.SnoozeUntilUtc != snooze)
        {
            return AutomationResult.NotApplicable();
        }

        // The "Personal reminders" switch was turned off after discovery: nothing is sent, the reminder
        // still moves on.
        if (!(await preferences.GetAsync(occurrence.UserId, cancellationToken)).IsEnabled(NotificationType.UserReminder))
        {
            return AutomationResult.Succeeded(saveArtifact: Advance(occurrence.UserId, of, ReminderOutcome.Disabled));
        }

        return AutomationResult.Succeeded(
            notification: new AutomationNotification(NotificationType.UserReminder, ResourceType, reminder.Id),
            saveArtifact: Advance(occurrence.UserId, of, ReminderOutcome.Notified));
    }

    // D-7: always true. False would roll the completion back and the occurrence would be retried forever.
    private Func<CancellationToken, Task<bool>> Advance(Guid userId, ReminderOccurrence of, ReminderOutcome outcome) =>
        async cancellationToken =>
        {
            await store.AdvanceAsync(userId, of, outcome, clock.GetUtcNow(), cancellationToken);
            return true;
        };

    private static bool TryFindZone(string? zoneId, out TimeZoneInfo zone)
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
