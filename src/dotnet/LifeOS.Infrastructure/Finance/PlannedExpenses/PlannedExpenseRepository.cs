using System.Data;
using LifeOS.Application.Finance.PlannedExpenses;
using LifeOS.Domain.Finance.PlannedExpenses;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LifeOS.Infrastructure.Finance.PlannedExpenses;

internal sealed class PlannedExpenseRepository(LifeOSDbContext db) : IPlannedExpenseRepository
{
    public async Task<PlannedExpenseRead> ReadAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var items = await db.Set<PlannedExpense>().AsNoTracking().Where(i => i.UserId == userId && i.ScheduledDate >= from && i.ScheduledDate <= to).ToListAsync(ct);
        var states = await db.Set<PlannedExpenseState>().AsNoTracking().Where(s => s.UserId == userId).ToListAsync(ct);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct);
        await tx.CommitAsync(ct);
        return new(items, states, accounts);
    }

    public async Task<PlannedExpenseSnapshot> GetAsync(Guid userId, Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var item = await db.Set<PlannedExpense>().AsNoTracking().SingleOrDefaultAsync(i => i.UserId == userId && i.Id == id, ct);
        var result = await Snapshot(userId, item, null, null, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<PlannedExpenseSnapshot> Snapshot(Guid userId, PlannedExpense? item, Guid? accountId, Guid? categoryId, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == userId && a.Id == (accountId ?? (item == null ? Guid.Empty : item.AccountId)), ct);
        var category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.UserId == userId && c.Id == (categoryId ?? (item == null ? Guid.Empty : item.CategoryId)), ct);
        var state = item is null ? null : await db.Set<PlannedExpenseState>().AsNoTracking().SingleOrDefaultAsync(s => s.UserId == userId && s.PlannedExpenseId == item.Id, ct);
        var actual = state?.TransactionId is { } id ? await db.Transactions.AsNoTracking().SingleOrDefaultAsync(t => t.UserId == userId && t.Id == id, ct) : null;
        return new(item, account, category, state, actual);
    }

    public async Task<PlannedExpenseResult> ExecuteAsync(Guid userId, Guid? id, Guid? accountId, Guid? categoryId,
        Func<PlannedExpenseSnapshot, PlannedExpenseResult> decide, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                var item = id is null ? null : (await db.Set<PlannedExpense>()
                    .FromSqlInterpolated($"SELECT * FROM planned_expenses WHERE id = {id} AND user_id = {userId} FOR UPDATE")
                    .AsNoTracking().ToListAsync(ct)).SingleOrDefault();
                var snapshot = await Snapshot(userId, item, accountId, categoryId, ct);
                var result = decide(snapshot);
                if (result.Status != PlannedExpenseResultStatus.Ok) return result;
                switch (result.Change)
                {
                    case PlannedExpenseChange.Save:
                        if (item is null) db.Add(result.Item!); else db.Update(result.Item!);
                        break;
                    case PlannedExpenseChange.Delete:
                        await db.Set<PlannedExpense>().Where(i => i.UserId == userId && i.Id == id).ExecuteDeleteAsync(ct);
                        break;
                    case PlannedExpenseChange.Process:
                        if (result.Transaction is not null) db.Add(result.Transaction);
                        db.Add(result.State!);
                        await Touch(userId, id!.Value, result.State!.CreatedAtUtc, ct);
                        break;
                    case PlannedExpenseChange.Restore:
                        await db.Set<PlannedExpenseState>().Where(s => s.UserId == userId && s.PlannedExpenseId == id).ExecuteDeleteAsync(ct);
                        await Touch(userId, id!.Value, result.Item!.UpdatedAtUtc, ct);
                        break;
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                Detach();
                return result;
            }
            catch (Exception e) when (Retryable(e)) { await tx.RollbackAsync(ct); Detach(); }
            catch (Exception e) when (PostgresErrors.IsForeignKeyViolation(e, new[] { PlannedExpenseConfiguration.AccountForeignKey, PlannedExpenseConfiguration.CategoryForeignKey }))
            {
                await tx.RollbackAsync(ct); Detach();
                return PlannedExpenseResult.Conflict("An account or category changed concurrently. Retry.");
            }
        }
        return PlannedExpenseResult.Conflict("The planned expense changed concurrently. Retry the same action.");
    }
    private Task<int> Touch(Guid userId, Guid id, DateTimeOffset now, CancellationToken ct) => db.Set<PlannedExpense>()
        .Where(i => i.UserId == userId && i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.UpdatedAtUtc, now), ct);
    private void Detach()
    {
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.Entity is PlannedExpense or PlannedExpenseState or Transaction).ToList()) e.State = EntityState.Detached;
    }
    private static bool Retryable(Exception e)
    {
        for (Exception? current = e; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }) return true;
        return PostgresErrors.IsUniqueViolation(e, PlannedExpenseStateConfiguration.ItemIndex);
    }
}
