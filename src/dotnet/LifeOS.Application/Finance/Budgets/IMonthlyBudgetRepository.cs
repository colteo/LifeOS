using LifeOS.Domain.Finance.Budgets;

namespace LifeOS.Application.Finance.Budgets;

public interface IMonthlyBudgetRepository
{
    Task<MonthlyBudget?> GetAsync(Guid userId, int year, int month, string currency, CancellationToken cancellationToken);
    // Reads the budget and locks its row (only that row) until the caller's unit of work ends, so no other
    // write to it can commit in between; a write already in progress is waited for and its result is
    // returned. Null, with nothing locked, when there is no budget. Must run inside a unit of work.
    Task<MonthlyBudget?> GetForUpdateAsync(Guid userId, int year, int month, string currency, CancellationToken cancellationToken);
    // Atomic set: concurrent requests cannot create duplicate budgets. Existing ids are preserved.
    Task SetAsync(MonthlyBudget budget, CancellationToken cancellationToken);
    Task DeleteAsync(Guid userId, int year, int month, string currency, CancellationToken cancellationToken);
}
