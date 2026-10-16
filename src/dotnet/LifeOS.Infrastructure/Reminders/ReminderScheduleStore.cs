using LifeOS.Application.Reminders;
using LifeOS.Domain.Reminders;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace LifeOS.Infrastructure.Reminders;

// REM-001 §6 on PostgreSQL. Discovery is one ids-and-schedule SELECT joining each reminder's local
// next_due_local with its owner's zone's local "now" (unnest of zone/local-now pairs), bounded by LIMIT
// and served by ix_reminders_due / ix_reminders_snooze. The only discovery writes are the conditional
// maintenance UPDATEs (§6.4). AdvanceAsync locks the row (FOR UPDATE) inside the completion transaction
// and lets the Domain decide the next occurrence.
internal sealed class ReminderScheduleStore(LifeOSDbContext db) : IReminderScheduleStore
{
    public async Task<IReadOnlyList<string>> GetUserTimeZoneIdsAsync(CancellationToken cancellationToken) =>
        await db.Users
            .AsNoTracking()
            .Where(user => user.TimeZoneId != null)
            .Select(user => user.TimeZoneId!)
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ReminderCandidate>> FindCandidatesAsync(IReadOnlyList<ReminderKind> kinds, IReadOnlyList<ZoneLocalNow> zones,
        DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
    {
        if (kinds.Count == 0 || zones.Count == 0 || limit <= 0)
        {
            return [];
        }

        var kindNames = kinds.Select(kind => kind.ToString()).ToArray();
        var zoneIds = zones.Select(zone => zone.TimeZoneId).ToArray();
        var localNows = LocalParameter("local_nows", zones.Select(zone => zone.LocalNow).ToArray());

        var rows = await db.Database
            .SqlQuery<CandidateRow>($"""
                SELECT r.id AS "ReminderId", r.user_id AS "UserId", u.time_zone_id AS "TimeZoneId", r.status AS "Status",
                       r.schedule_kind AS "ScheduleKind", r.once_date AS "OnceDate", r.weekdays::int AS "Weekdays", r.local_time AS "LocalTime",
                       r.next_due_local AS "NextDueLocal", r.snooze_until_utc AS "SnoozeUntilUtc"
                FROM reminders AS r
                JOIN users AS u ON u.id = r.user_id
                JOIN unnest({zoneIds}::text[], {localNows}) AS zone(time_zone_id, local_now)
                  ON u.time_zone_id = zone.time_zone_id
                WHERE r.status <> 'Paused'
                  AND r.kind = ANY({kindNames}::text[])
                  AND ((r.status = 'Active' AND r.next_due_local <= zone.local_now) OR r.snooze_until_utc <= {nowUtc})
                  AND NOT EXISTS (
                        SELECT 1 FROM notification_preferences AS p
                        WHERE p.user_id = r.user_id
                          AND ((r.kind = 'Custom' AND NOT p.user_reminders_enabled)
                            OR (r.kind = 'HabitIncomplete' AND NOT p.habit_reminders_enabled)
                            OR (r.kind = 'HydrationGoal' AND NOT p.hydration_reminders_enabled)))
                ORDER BY r.next_due_local NULLS LAST, r.snooze_until_utc, r.id
                LIMIT {limit}
                """)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new ReminderCandidate(
                row.ReminderId,
                row.UserId,
                row.TimeZoneId,
                Enum.Parse<ReminderStatus>(row.Status),
                ReminderSchedule.FromParts(Enum.Parse<ReminderScheduleKind>(row.ScheduleKind), row.OnceDate, row.Weekdays, row.LocalTime),
                row.NextDueLocal is { } next ? DateTime.SpecifyKind(next, DateTimeKind.Unspecified) : null,
                row.SnoozeUntilUtc))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetExecutionsAsync(string automationType,
        IReadOnlyList<(Guid UserId, string OccurrenceKey)> occurrences, CancellationToken cancellationToken)
    {
        if (occurrences.Count == 0)
        {
            return new Dictionary<string, bool>();
        }

        var userIds = occurrences.Select(occurrence => occurrence.UserId).ToArray();
        var keys = occurrences.Select(occurrence => occurrence.OccurrenceKey).ToArray();

        // Served by the (user_id, automation_type, occurrence_key) unique index.
        var rows = await db.Database
            .SqlQuery<ExecutionRow>($"""
                SELECT e.occurrence_key AS "OccurrenceKey", e.status IN ('Succeeded', 'FailedFinal') AS "Terminal"
                FROM unnest({userIds}::uuid[], {keys}::text[]) AS o(user_id, occurrence_key)
                JOIN automation_executions AS e
                  ON e.user_id = o.user_id AND e.automation_type = {automationType} AND e.occurrence_key = o.occurrence_key
                """)
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.OccurrenceKey, row => row.Terminal, StringComparer.Ordinal);
    }

    public async Task SkipMissedAsync(IReadOnlyList<MissedOccurrence> occurrences, IReadOnlyList<MissedSnooze> snoozes, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var now = Reminder.ToStoredPrecision(nowUtc);

        foreach (var missed in occurrences)
        {
            var expected = LocalParameter("expected", missed.ExpectedNextDueLocal);
            var next = LocalParameter("next", missed.NextDueLocal);
            var status = missed.NextDueLocal is null ? nameof(ReminderStatus.Completed) : nameof(ReminderStatus.Active);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reminders
                SET next_due_local = {next}, status = {status}, last_outcome = 'Missed', updated_at_utc = {now}
                WHERE id = {missed.ReminderId} AND user_id = {missed.UserId} AND status = 'Active' AND next_due_local = {expected}
                """, cancellationToken);
        }

        foreach (var missed in snoozes)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reminders
                SET snooze_until_utc = NULL, last_outcome = 'Missed', updated_at_utc = {now}
                WHERE id = {missed.ReminderId} AND user_id = {missed.UserId} AND snooze_until_utc = {missed.SnoozeUtc}
                """, cancellationToken);
        }
    }

    public Task<Reminder?> FindAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken) =>
        db.Reminders.AsNoTracking().SingleOrDefaultAsync(reminder => reminder.UserId == userId && reminder.Id == reminderId, cancellationToken);

    public async Task AdvanceAsync(Guid userId, ReminderOccurrence occurrence, ReminderOutcome outcome, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Advance a reminder inside the completion transaction.");
        }

        // The row lock orders this against a concurrent edit, pause or snooze; the fresh row is read
        // after it (never a stale tracked copy).
        foreach (var entry in db.ChangeTracker.Entries<Reminder>().Where(entry => entry.Entity.Id == occurrence.ReminderId).ToList())
        {
            entry.State = EntityState.Detached;
        }

        var reminder = (await db.Reminders
                .FromSqlInterpolated($"SELECT * FROM reminders WHERE id = {occurrence.ReminderId} AND user_id = {userId} FOR UPDATE")
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (reminder is null)
        {
            return;
        }

        if (occurrence.SnoozeUtc is { } snooze)
        {
            reminder.AdvanceSnooze(snooze, outcome, nowUtc);
        }
        else
        {
            reminder.Advance(occurrence.Local!.Value, outcome, nowUtc);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // Local wall-clock values are "timestamp without time zone": EF's default DateTime mapping is timestamptz.
    private static NpgsqlParameter LocalParameter(string name, DateTime? value) =>
        new(name, NpgsqlDbType.Timestamp) { Value = value is { } local ? DateTime.SpecifyKind(local, DateTimeKind.Unspecified) : DBNull.Value };

    private static NpgsqlParameter LocalParameter(string name, DateTime[] values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Timestamp) { Value = values.Select(value => DateTime.SpecifyKind(value, DateTimeKind.Unspecified)).ToArray() };

    private sealed class CandidateRow
    {
        public Guid ReminderId { get; set; }

        public Guid UserId { get; set; }

        public string TimeZoneId { get; set; } = "";

        public string Status { get; set; } = "";

        public string ScheduleKind { get; set; } = "";

        public DateOnly? OnceDate { get; set; }

        public int? Weekdays { get; set; }

        public TimeOnly LocalTime { get; set; }

        public DateTime? NextDueLocal { get; set; }

        public DateTimeOffset? SnoozeUntilUtc { get; set; }
    }

    private sealed class ExecutionRow
    {
        public string OccurrenceKey { get; set; } = "";

        public bool Terminal { get; set; }
    }
}
