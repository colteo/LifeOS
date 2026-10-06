using LifeOS.Application.Finance.Reminders;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Finance.Reminders;

// AUTO-003A discovery on PostgreSQL: one ids-only, LIMITed SELECT per reminder type over the users of
// the open zones (AUTO-001 §7). Read-only: no Finance row is written or locked.
//
// The NOT EXISTS on automation_executions rebuilds the handler's occurrence key in SQL:
//   recurring        "<rule id>:YYYY-MM"            (RecurringTransactionReminderHandler.OccurrenceKey)
//   planned expense  "<planned expense id>:YYYY-MM-DD" (PlannedExpenseReminderHandler.OccurrenceKey)
// uuid::text is the lowercase "D" format, as Guid.ToString("D").
internal sealed class FinanceReminderRepository(LifeOSDbContext db) : IFinanceReminderRepository
{
    public async Task<IReadOnlyList<string>> GetUserTimeZoneIdsAsync(CancellationToken cancellationToken) =>
        await db.Users
            .AsNoTracking()
            .Where(user => user.TimeZoneId != null)
            .Select(user => user.TimeZoneId!)
            .Distinct()
            .ToListAsync(cancellationToken);

    // FIN-004/ADR-009 occurrence of the open date's logical month: inside [start, end], requested day
    // clamped to the month's last day equals the open date, and no Confirmed/Skipped state.
    public async Task<IReadOnlyList<DueRecurringReminder>> FindDueRecurringAsync(
        string automationType,
        IReadOnlyList<FinanceReminderOpenZone> openZones,
        int limit,
        CancellationToken cancellationToken)
    {
        if (openZones.Count == 0 || limit <= 0)
        {
            return [];
        }

        var zones = openZones.Select(zone => zone.TimeZoneId).ToArray();
        var dates = openZones.Select(zone => zone.LocalDate).ToArray();

        var rows = await db.Database
            .SqlQuery<RecurringRow>($"""
                SELECT u.id AS "UserId", u.time_zone_id AS "TimeZoneId", r.id AS "RuleId", open_zone.local_date AS "LocalDate"
                FROM users AS u
                JOIN unnest({zones}::text[], {dates}::date[]) AS open_zone(time_zone_id, local_date)
                  ON u.time_zone_id = open_zone.time_zone_id
                CROSS JOIN LATERAL (
                    SELECT EXTRACT(YEAR FROM open_zone.local_date)::int AS year,
                           EXTRACT(MONTH FROM open_zone.local_date)::int AS month,
                           EXTRACT(DAY FROM open_zone.local_date)::int AS day,
                           EXTRACT(DAY FROM open_zone.local_date + 1)::int = 1 AS is_last_day) AS d
                JOIN recurring_transaction_rules AS r
                  ON r.user_id = u.id
                 AND r.start_year * 12 + r.start_month <= d.year * 12 + d.month
                 AND (r.end_year IS NULL OR r.end_year * 12 + r.end_month >= d.year * 12 + d.month)
                 AND (r.day_of_month = d.day OR (d.is_last_day AND r.day_of_month > d.day))
                WHERE NOT EXISTS (
                        SELECT 1 FROM notification_preferences AS p
                        WHERE p.user_id = u.id AND NOT p.recurring_transaction_reminders_enabled)
                  AND NOT EXISTS (
                        SELECT 1 FROM recurring_transaction_occurrences AS s
                        WHERE s.recurring_rule_id = r.id AND s.year = d.year AND s.month = d.month)
                  AND NOT EXISTS (
                        SELECT 1 FROM automation_executions AS e
                        WHERE e.user_id = u.id AND e.automation_type = {automationType}
                          AND e.occurrence_key = r.id::text || ':' || to_char(open_zone.local_date, 'YYYY-MM'))
                ORDER BY u.id, r.id
                LIMIT {limit}
                """)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new DueRecurringReminder(row.UserId, row.TimeZoneId, row.RuleId, row.LocalDate)).ToList();
    }

    // FIN-005/ADR-010: scheduled on the open date and unresolved (no Confirmed/Cancelled state).
    public async Task<IReadOnlyList<DuePlannedExpenseReminder>> FindDuePlannedExpensesAsync(
        string automationType,
        IReadOnlyList<FinanceReminderOpenZone> openZones,
        int limit,
        CancellationToken cancellationToken)
    {
        if (openZones.Count == 0 || limit <= 0)
        {
            return [];
        }

        var zones = openZones.Select(zone => zone.TimeZoneId).ToArray();
        var dates = openZones.Select(zone => zone.LocalDate).ToArray();

        var rows = await db.Database
            .SqlQuery<PlannedExpenseRow>($"""
                SELECT u.id AS "UserId", u.time_zone_id AS "TimeZoneId", pe.id AS "PlannedExpenseId", open_zone.local_date AS "LocalDate"
                FROM users AS u
                JOIN unnest({zones}::text[], {dates}::date[]) AS open_zone(time_zone_id, local_date)
                  ON u.time_zone_id = open_zone.time_zone_id
                JOIN planned_expenses AS pe
                  ON pe.user_id = u.id AND pe.scheduled_date = open_zone.local_date
                WHERE NOT EXISTS (
                        SELECT 1 FROM notification_preferences AS p
                        WHERE p.user_id = u.id AND NOT p.planned_expense_reminders_enabled)
                  AND NOT EXISTS (
                        SELECT 1 FROM planned_expense_states AS s
                        WHERE s.planned_expense_id = pe.id)
                  AND NOT EXISTS (
                        SELECT 1 FROM automation_executions AS e
                        WHERE e.user_id = u.id AND e.automation_type = {automationType}
                          AND e.occurrence_key = pe.id::text || ':' || to_char(open_zone.local_date, 'YYYY-MM-DD'))
                ORDER BY u.id, pe.id
                LIMIT {limit}
                """)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new DuePlannedExpenseReminder(row.UserId, row.TimeZoneId, row.PlannedExpenseId, row.LocalDate)).ToList();
    }

    private sealed class RecurringRow
    {
        public Guid UserId { get; set; }

        public string TimeZoneId { get; set; } = "";

        public Guid RuleId { get; set; }

        public DateOnly LocalDate { get; set; }
    }

    private sealed class PlannedExpenseRow
    {
        public Guid UserId { get; set; }

        public string TimeZoneId { get; set; } = "";

        public Guid PlannedExpenseId { get; set; }

        public DateOnly LocalDate { get; set; }
    }
}
