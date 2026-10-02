using LifeOS.Application.Finance.Budgets;
using LifeOS.Domain.Finance.Budgets;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Finance.Budgets;

internal sealed class MonthlyBudgetRepository(LifeOSDbContext db) : IMonthlyBudgetRepository
{
    public Task<MonthlyBudget?> GetAsync(Guid userId, int year, int month, string currency, CancellationToken cancellationToken) =>
        db.Set<MonthlyBudget>().AsNoTracking().SingleOrDefaultAsync(
            b => b.UserId == userId && b.Year == year && b.Month == month && b.Currency == currency, cancellationToken);

    public async Task SetAsync(MonthlyBudget budget, CancellationToken cancellationToken)
    {
        // PostgreSQL performs insert/update atomically, including concurrent first-time sets.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO monthly_budgets (id, user_id, year, month, currency, amount)
            VALUES ({budget.Id}, {budget.UserId}, {budget.Year}, {budget.Month}, {budget.Currency}, {budget.Amount})
            ON CONFLICT (user_id, year, month, currency) DO UPDATE SET amount = EXCLUDED.amount
            """, cancellationToken);
    }

    public async Task DeleteAsync(Guid userId, int year, int month, string currency, CancellationToken cancellationToken) =>
        await db.Set<MonthlyBudget>().Where(b => b.UserId == userId && b.Year == year && b.Month == month && b.Currency == currency)
            .ExecuteDeleteAsync(cancellationToken);
}
