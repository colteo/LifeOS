using LifeOS.Domain.Reminders;

namespace LifeOS.Application.Reminders;

// A valid IANA zone and its local wall-clock time now, to the minute.
public sealed record ZoneLocalNow(string TimeZoneId, DateTime LocalNow);

// A reminder that may have an occurrence due (discovery prefilter, §6.2), with its owner's zone.
public sealed record ReminderCandidate(
    Guid ReminderId,
    Guid UserId,
    string TimeZoneId,
    ReminderStatus Status,
    ReminderSchedule Schedule,
    DateTime? NextDueLocal,
    DateTimeOffset? SnoozeUntilUtc);

// A scheduled occurrence found after its lateness, or whose execution is already terminal: the reminder
// moves on to NextDueLocal (null: a one-time reminder is completed) as Missed, but only while its
// next_due_local is still ExpectedNextDueLocal (a concurrent edit or completion wins).
public sealed record MissedOccurrence(Guid UserId, Guid ReminderId, DateTime ExpectedNextDueLocal, DateTime? NextDueLocal);

// The same for a snooze: it is cleared only while it is still SnoozeUtc.
public sealed record MissedSnooze(Guid UserId, Guid ReminderId, DateTimeOffset SnoozeUtc);

// REM-001 §6/§8: the automation side of the Reminders module. Discovery is read-only apart from the
// bounded, conditional maintenance writes (§6.4); AdvanceAsync runs inside the fenced completion
// transaction (AUTO-002 saveArtifact) on the scope's unit of work.
public interface IReminderScheduleStore
{
    // The distinct non-null users.time_zone_id values.
    Task<IReadOnlyList<string>> GetUserTimeZoneIdsAsync(CancellationToken cancellationToken);

    // At most `limit` reminders of these kinds whose owner is in one of these zones and has not muted the
    // kind's notification type (personal, habit or water reminders), and that are not Paused and either
    // Active with next_due_local ≤ the zone's local now or with snooze_until_utc ≤ nowUtc. Oldest next
    // occurrence first.
    Task<IReadOnlyList<ReminderCandidate>> FindCandidatesAsync(IReadOnlyList<ReminderKind> kinds, IReadOnlyList<ZoneLocalNow> zones,
        DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken);

    // Of these occurrence keys of automationType, the ones that already have an execution, with whether
    // it is terminal (Succeeded or FailedFinal).
    Task<IReadOnlyDictionary<string, bool>> GetExecutionsAsync(string automationType, IReadOnlyList<(Guid UserId, string OccurrenceKey)> occurrences,
        CancellationToken cancellationToken);

    // Idempotent conditional updates; rows changed meanwhile are left alone.
    Task SkipMissedAsync(IReadOnlyList<MissedOccurrence> occurrences, IReadOnlyList<MissedSnooze> snoozes, DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    // The reminder as stored now (not tracked), or null.
    Task<Reminder?> FindAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken);

    // Locks the reminder, applies Reminder.Advance / AdvanceSnooze for the occurrence and saves it. Must
    // run inside the completion transaction. A reminder deleted meanwhile is left alone.
    Task AdvanceAsync(Guid userId, ReminderOccurrence occurrence, ReminderOutcome outcome, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}
