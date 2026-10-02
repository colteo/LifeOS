using System.Data;
using LifeOS.Application.Finance.Recurring;
using LifeOS.Domain.Finance.Recurring;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LifeOS.Infrastructure.Finance.Recurring;

internal sealed class RecurringRepository(LifeOSDbContext db) : IRecurringRepository
{
    public async Task<RecurringRead> ReadAsync(Guid userId, int fromYear, int fromMonth, int toYear, int toMonth, CancellationToken ct,
        DateTimeOffset? transactionsFromUtc = null, DateTimeOffset? transactionsToUtc = null)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var rules = await db.Set<RecurringTransactionRule>().AsNoTracking().Where(r => r.UserId == userId).ToListAsync(ct);
        var from = fromYear * 12 + fromMonth; var to = toYear * 12 + toMonth;
        var states = await db.Set<RecurringOccurrenceState>().AsNoTracking().Where(s => s.UserId == userId
            && s.Year * 12 + s.Month >= from && s.Year * 12 + s.Month <= to).ToListAsync(ct);
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct);
        var movements = transactionsFromUtc is not null && transactionsToUtc is not null
            ? await db.Transactions.AsNoTracking().Where(t => t.UserId == userId && t.OccurredAtUtc >= transactionsFromUtc
                && t.OccurredAtUtc < transactionsToUtc).ToListAsync(ct) : null;
        await snapshot.CommitAsync(ct);
        return new(rules, states, accounts, movements);
    }

    public async Task<RecurringResult> ExecuteAsync(Guid userId, Guid? ruleId, int? year, int? month,
        Guid? accountId, Guid? categoryId, Func<RecurringSnapshot, RecurringResult> decide, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                var rule = ruleId is null ? null : (await db.Set<RecurringTransactionRule>()
                    .FromSqlInterpolated($"SELECT * FROM recurring_transaction_rules WHERE id = {ruleId} AND user_id = {userId} FOR UPDATE")
                    .AsNoTracking().ToListAsync(ct)).SingleOrDefault();
                var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.UserId == userId && a.Id == (accountId ?? (rule == null ? Guid.Empty : rule.AccountId)), ct);
                var category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.UserId == userId && c.Id == (categoryId ?? (rule == null ? Guid.Empty : rule.CategoryId)), ct);
                var state = await db.Set<RecurringOccurrenceState>().AsNoTracking().SingleOrDefaultAsync(s => s.UserId == userId
                    && s.RecurringRuleId == ruleId && s.Year == year && s.Month == month, ct);
                var actual = state?.TransactionId is { } id
                    ? await db.Transactions.AsNoTracking().SingleOrDefaultAsync(t => t.UserId == userId && t.Id == id, ct) : null;
                var result = decide(new(rule, account, category, state, actual));
                if (result.Status != RecurringResultStatus.Ok) return result;
                switch (result.Change)
                {
                    case RecurringChange.SaveRule:
                        if (rule is null) db.Add(result.Rule!); else db.Update(result.Rule!);
                        break;
                    case RecurringChange.DeleteRule:
                        await db.Set<RecurringTransactionRule>().Where(r => r.Id == ruleId && r.UserId == userId).ExecuteDeleteAsync(ct);
                        break;
                    case RecurringChange.SaveState:
                        if (result.Transaction is not null) db.Add(result.Transaction);
                        db.Add(result.State!);
                        break;
                    case RecurringChange.Restore:
                        await db.Set<RecurringOccurrenceState>().Where(s => s.Id == state!.Id && s.UserId == userId).ExecuteDeleteAsync(ct);
                        break;
                }
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                Detach();
                return result;
            }
            catch (Exception e) when (Retryable(e)) { await tx.RollbackAsync(ct); Detach(); }
            catch (Exception e) when (PostgresErrors.IsForeignKeyViolation(e, new[] { RecurringRuleConfiguration.AccountForeignKey, RecurringRuleConfiguration.CategoryForeignKey }))
            {
                await tx.RollbackAsync(ct); Detach();
                return RecurringResult.Conflict("An account or category changed concurrently. Retry.");
            }
        }
        return RecurringResult.Conflict("The recurring item changed concurrently. Retry the same action.");
    }

    private void Detach()
    {
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.Entity is RecurringTransactionRule or RecurringOccurrenceState or Transaction).ToList())
            e.State = EntityState.Detached;
    }
    private static bool Retryable(Exception e)
    {
        for (Exception? current = e; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected }) return true;
        return PostgresErrors.IsUniqueViolation(e, RecurringOccurrenceConfiguration.MonthIndex);
    }
}
