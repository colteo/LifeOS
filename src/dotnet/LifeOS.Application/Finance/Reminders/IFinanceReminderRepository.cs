namespace LifeOS.Application.Finance.Reminders;

// A zone whose daily reminder occurrence is open now, and that occurrence's local date.
public sealed record FinanceReminderOpenZone(string TimeZoneId, DateOnly LocalDate);

// A recurring rule whose occurrence is scheduled on the open local date of its owner's zone.
public sealed record DueRecurringReminder(Guid UserId, string TimeZoneId, Guid RuleId, DateOnly LocalDate);

// A one-off planned expense scheduled on the open local date of its owner's zone.
public sealed record DuePlannedExpenseReminder(Guid UserId, string TimeZoneId, Guid PlannedExpenseId, DateOnly LocalDate);

// AUTO-003A read-only discovery of Finance reminders (AUTO-001 §7 zone buckets). Each query is one
// ids-only, LIMITed statement over the users of the open zones. Never writes Finance data.
public interface IFinanceReminderRepository
{
    // The distinct non-null users.time_zone_id values.
    Task<IReadOnlyList<string>> GetUserTimeZoneIdsAsync(CancellationToken cancellationToken);

    // At most `limit` (user, rule) pairs, by user then rule, where the user is in an open zone, has not
    // disabled recurring reminders, and the rule's logical month of the open date (FIN-004/ADR-009):
    // - is inside the rule's start/end range,
    // - is scheduled on that date (day of month, clamped to the month's last day),
    // - has no Confirmed/Skipped state,
    // - has no execution of automationType with key RecurringTransactionReminderHandler.OccurrenceKey.
    Task<IReadOnlyList<DueRecurringReminder>> FindDueRecurringAsync(
        string automationType,
        IReadOnlyList<FinanceReminderOpenZone> openZones,
        int limit,
        CancellationToken cancellationToken);

    // At most `limit` (user, planned expense) pairs, by user then expense, where the user is in an open
    // zone, has not disabled planned-expense reminders, and the expense (FIN-005/ADR-010) is scheduled on
    // the open date, has no Confirmed/Cancelled state, and has no execution of automationType with key
    // PlannedExpenseReminderHandler.OccurrenceKey.
    Task<IReadOnlyList<DuePlannedExpenseReminder>> FindDuePlannedExpensesAsync(
        string automationType,
        IReadOnlyList<FinanceReminderOpenZone> openZones,
        int limit,
        CancellationToken cancellationToken);
}
