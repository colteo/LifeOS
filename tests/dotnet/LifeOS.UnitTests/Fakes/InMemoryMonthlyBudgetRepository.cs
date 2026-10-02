using LifeOS.Application.Finance.Budgets;
using LifeOS.Domain.Finance.Budgets;

namespace LifeOS.UnitTests.Fakes;

internal sealed class InMemoryMonthlyBudgetRepository : IMonthlyBudgetRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(Guid, int, int, string), MonthlyBudget> _budgets = [];
    public Task<MonthlyBudget?> GetAsync(Guid userId, int year, int month, string currency, CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult(_budgets.GetValueOrDefault((userId, year, month, currency)));
    }
    public Task SetAsync(MonthlyBudget budget, CancellationToken cancellationToken)
    {
        lock (_lock) _budgets[(budget.UserId, budget.Year, budget.Month, budget.Currency)] = budget;
        return Task.CompletedTask;
    }
    public Task DeleteAsync(Guid userId, int year, int month, string currency, CancellationToken cancellationToken)
    {
        lock (_lock) _budgets.Remove((userId, year, month, currency));
        return Task.CompletedTask;
    }
}
