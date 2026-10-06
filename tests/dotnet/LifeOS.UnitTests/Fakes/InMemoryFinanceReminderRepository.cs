using LifeOS.Application.Finance.Reminders;

namespace LifeOS.UnitTests.Fakes;

// Returns the configured candidates whose zone and date are open, minus those already claimed in the
// execution store (the PostgreSQL NOT EXISTS). The Finance due rules themselves are proven against
// PostgreSQL in FinanceReminderPersistenceTests.
internal sealed class InMemoryFinanceReminderRepository(InMemoryAutomationExecutionStore? executions = null) : IFinanceReminderRepository
{
    public List<string> TimeZoneIds { get; } = [];

    public List<DueRecurringReminder> Recurring { get; } = [];

    public List<DuePlannedExpenseReminder> PlannedExpenses { get; } = [];

    public Task<IReadOnlyList<string>> GetUserTimeZoneIdsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(TimeZoneIds.Distinct().ToList());

    public Task<IReadOnlyList<DueRecurringReminder>> FindDueRecurringAsync(
        string automationType, IReadOnlyList<FinanceReminderOpenZone> openZones, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DueRecurringReminder>>(Recurring
            .Where(item => openZones.Contains(new FinanceReminderOpenZone(item.TimeZoneId, item.LocalDate))
                && !Claimed(item.UserId, automationType, RecurringTransactionReminderHandler.OccurrenceKey(item.RuleId, item.LocalDate.Year, item.LocalDate.Month)))
            .OrderBy(item => item.UserId).ThenBy(item => item.RuleId)
            .Take(limit)
            .ToList());

    public Task<IReadOnlyList<DuePlannedExpenseReminder>> FindDuePlannedExpensesAsync(
        string automationType, IReadOnlyList<FinanceReminderOpenZone> openZones, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DuePlannedExpenseReminder>>(PlannedExpenses
            .Where(item => openZones.Contains(new FinanceReminderOpenZone(item.TimeZoneId, item.LocalDate))
                && !Claimed(item.UserId, automationType, PlannedExpenseReminderHandler.OccurrenceKey(item.PlannedExpenseId, item.LocalDate)))
            .OrderBy(item => item.UserId).ThenBy(item => item.PlannedExpenseId)
            .Take(limit)
            .ToList());

    private bool Claimed(Guid userId, string automationType, string key) =>
        executions?.Rows.Any(row => row.UserId == userId && row.AutomationType == automationType && row.OccurrenceKey == key) ?? false;
}
