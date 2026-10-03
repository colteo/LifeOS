using System.Data;
using LifeOS.Application.Finance.Budgets;
using LifeOS.Domain.Finance.PlannedExpenses;
using LifeOS.Domain.Finance.Recurring;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Finance.Budgets;

internal sealed class FinancePlanningSnapshotRepository(LifeOSDbContext db) : IFinancePlanningSnapshotRepository
{
    public async Task<FinancePlanningSnapshot> ReadAsync(Guid userId, int year, int month,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var rules = await db.Set<RecurringTransactionRule>().AsNoTracking().Where(r => r.UserId == userId).ToListAsync(ct);
        var states = await db.Set<RecurringOccurrenceState>().AsNoTracking().Where(s => s.UserId == userId && s.Year == year && s.Month == month).ToListAsync(ct);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct);
        var from = new DateOnly(year, month, 1); var to = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var planned = await db.Set<PlannedExpense>().AsNoTracking().Where(i => i.UserId == userId && i.ScheduledDate >= from && i.ScheduledDate <= to).ToListAsync(ct);
        var plannedStates = await db.Set<PlannedExpenseState>().AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct);
        var movements = await db.Transactions.AsNoTracking().Where(t => t.UserId == userId && t.OccurredAtUtc >= fromUtc && t.OccurredAtUtc < toUtc).ToListAsync(ct);
        await snapshot.CommitAsync(ct);
        return new(new(rules, states, accounts), new(planned, plannedStates, accounts), movements);
    }
}
