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

    public async Task<MonthlyBudget?> GetForUpdateAsync(Guid userId, int year, int month, string currency, CancellationToken cancellationToken)
    {
        // Outside a transaction the lock would be released at once.
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("A budget row lock needs a unit of work.");
        }

        // Read Committed: if another transaction holds the row, PostgreSQL waits for it and returns the
        // row as committed by it (or nothing, if it was deleted).
        var rows = await db.Set<MonthlyBudget>().FromSqlInterpolated($"""
            SELECT * FROM monthly_budgets
            WHERE user_id = {userId} AND year = {year} AND month = {month} AND currency = {currency}
            FOR UPDATE
            """).AsNoTracking().ToListAsync(cancellationToken);

        return rows.SingleOrDefault();
    }

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
