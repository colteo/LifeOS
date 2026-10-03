using LifeOS.Application.Finance.PlannedExpenses;
using LifeOS.Application.Finance.Recurring;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Budgets;

// Budget confirmation transitions require actuals and both kinds of planning from one snapshot.
public sealed record FinancePlanningSnapshot(RecurringRead Recurring, PlannedExpenseRead PlannedExpenses,
    IReadOnlyList<Transaction> Transactions);

public interface IFinancePlanningSnapshotRepository
{
    Task<FinancePlanningSnapshot> ReadAsync(Guid userId, int year, int month,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}
