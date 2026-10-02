using LifeOS.Domain.Finance.Budgets;

namespace LifeOS.Application.Finance.Budgets;

public interface IMonthlyBudgetRepository
{
    Task<MonthlyBudget?> GetAsync(Guid userId, int year, int month, string currency, CancellationToken cancellationToken);
    // Atomic set: concurrent requests cannot create duplicate budgets. Existing ids are preserved.
    Task SetAsync(MonthlyBudget budget, CancellationToken cancellationToken);
    Task DeleteAsync(Guid userId, int year, int month, string currency, CancellationToken cancellationToken);
}
