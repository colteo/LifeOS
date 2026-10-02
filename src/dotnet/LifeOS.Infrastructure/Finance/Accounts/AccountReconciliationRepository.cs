using System.Data;
using LifeOS.Application.Finance.Accounts.ReconcileAccount;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LifeOS.Infrastructure.Finance.Accounts;

internal sealed class AccountReconciliationRepository(LifeOSDbContext db) : IAccountReconciliationRepository, IAccountBalanceAdjustmentRepository
{
    public async Task<IReadOnlyList<AccountBalanceAdjustment>> GetAllAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Set<AccountBalanceAdjustment>().AsNoTracking().Where(a => a.UserId == userId).ToListAsync(cancellationToken);

    public async Task<ReconcileAccountResult> ExecuteAsync(Guid userId, Guid accountId, Guid requestId,
        Func<ReconciliationSnapshot, ReconcileAccountResult> decide, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            try
            {
                // Owned row lock: no unscoped lookup, even for retry metadata.
                var account = (await db.Accounts.FromSqlInterpolated($"SELECT * FROM accounts WHERE id = {accountId} AND user_id = {userId} FOR UPDATE")
                    .AsNoTracking().ToListAsync(cancellationToken)).SingleOrDefault();
                if (account is null)
                    return decide(new(null, null, [], [], null));
                var receipt = await db.Set<AccountReconciliation>().AsNoTracking().SingleOrDefaultAsync(
                    r => r.UserId == userId && r.AccountId == accountId && r.RequestId == requestId, cancellationToken);
                var opening = await db.OpeningBalances.AsNoTracking().SingleOrDefaultAsync(
                    o => o.UserId == userId && o.AccountId == accountId, cancellationToken);
                var movements = await db.Transactions.AsNoTracking().Where(t => t.UserId == userId
                    && (t.AccountId == accountId || t.SourceAccountId == accountId || t.DestinationAccountId == accountId))
                    .ToListAsync(cancellationToken);
                var adjustments = await db.Set<AccountBalanceAdjustment>().AsNoTracking().Where(
                    a => a.UserId == userId && a.AccountId == accountId).ToListAsync(cancellationToken);
                var result = decide(new(account, opening, movements, adjustments, receipt));
                if (result.Status != ReconcileAccountStatus.Ok) return result;
                if (!result.IsReplay)
                {
                    db.Add(result.Receipt!);
                    if (result.Adjustment is { } adjustment) db.Add(adjustment);
                    await db.SaveChangesAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch (Exception exception) when (IsRetryable(exception))
            {
                await transaction.RollbackAsync(cancellationToken);
                // Only reconciliation entities were tracked by this focused operation.
                foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is AccountReconciliation or AccountBalanceAdjustment).ToList())
                    entry.State = EntityState.Detached;
            }
        }
        return ReconcileAccountResult.Failure(ReconcileAccountStatus.Conflict, "The account changed concurrently. Retry with the same request key.");
    }

    private static bool IsRetryable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
                return true;
        return PostgresErrors.IsUniqueViolation(exception, AccountReconciliationConfiguration.RequestIndexName);
    }
}
