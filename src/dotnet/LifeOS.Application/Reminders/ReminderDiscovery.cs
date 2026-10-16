using LifeOS.Application.Automation;
using LifeOS.Application.Users.SetTimeZone;
using LifeOS.Domain.Reminders;

namespace LifeOS.Application.Reminders;

// REM-001 §6.2-6.4, shared by both reminder handlers (D-4: "Reminder" for Custom, 6 h lateness;
// "ConditionalReminder" for the nudges, 2 h): the same discovery over the kinds a handler owns, and the
// same checks at the start of an execution.
//
// Discovery joins each reminder's local next_due_local with its owner's zone's local "now" (D-3), so a
// zone change needs no rewrite. Of the occurrences missed since next_due_local only the latest is
// delivered; missed ones beyond the handler's lateness are skipped as Missed, never replayed (D-14).
internal static class ReminderDiscovery
{
    public const string InvalidOccurrenceKeyCode = "InvalidOccurrenceKey";
    public const string InvalidTimeZoneCode = "InvalidTimeZone";

    public static async Task<IReadOnlyList<DueOccurrence>> FindDueAsync(IReminderScheduleStore store, string automationType, TimeSpan lateness,
        IReadOnlyList<ReminderKind> kinds, DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
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
            kinds, zones.Select(entry => new ZoneLocalNow(entry.Key, entry.Value.LocalNow)).ToList(), now, limit * 2, cancellationToken);

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
                if (now >= snooze + lateness)
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

                if (now >= dueAtUtc + lateness)
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
            : await store.GetExecutionsAsync(automationType, due.Select(item => (item.Occurrence.UserId, item.Occurrence.OccurrenceKey)).ToList(),
                cancellationToken);
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

    // The start of an execution: the key and zone must be valid (permanent failure otherwise) and the
    // occurrence must still exist. Re-read now: deleted, paused, edited or snoozed again since discovery
    // (or of a kind this handler does not own) means this occurrence no longer exists: NotApplicable.
    public static async Task<(AutomationResult? Done, Reminder? Reminder, ReminderOccurrence? Of, TimeZoneInfo Zone)> StartAsync(
        IReminderScheduleStore store, AutomationOccurrence occurrence, Func<ReminderKind, bool> owns, CancellationToken cancellationToken)
    {
        if (!ReminderOccurrenceKey.TryParse(occurrence.OccurrenceKey, out var of))
        {
            return (AutomationResult.PermanentFailure(InvalidOccurrenceKeyCode), null, null, TimeZoneInfo.Utc);
        }

        if (!TryFindZone(occurrence.TimeZoneId, out var zone))
        {
            return (AutomationResult.PermanentFailure(InvalidTimeZoneCode), null, null, TimeZoneInfo.Utc);
        }

        var reminder = await store.FindAsync(occurrence.UserId, of.ReminderId, cancellationToken);

        if (reminder is null || reminder.Status == ReminderStatus.Paused || !owns(reminder.Kind)
            || (of.Local is { } local && (reminder.Status != ReminderStatus.Active || !reminder.Schedule.Includes(local)))
            || (of.SnoozeUtc is { } snooze && reminder.SnoozeUntilUtc != snooze))
        {
            return (AutomationResult.NotApplicable(), null, null, zone);
        }

        return (null, reminder, of, zone);
    }

    // D-7: always true. False would roll the completion back and the occurrence would be retried forever.
    public static Func<CancellationToken, Task<bool>> Advance(IReminderScheduleStore store, TimeProvider clock, Guid userId, ReminderOccurrence of,
        ReminderOutcome outcome) =>
        async cancellationToken =>
        {
            await store.AdvanceAsync(userId, of, outcome, clock.GetUtcNow(), cancellationToken);
            return true;
        };

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
