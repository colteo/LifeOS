using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Accounts.ReconcileAccount;

public sealed record ReconciliationSnapshot(Account? Account, OpeningBalance? OpeningBalance,
    IReadOnlyList<Transaction> Transactions, IReadOnlyList<AccountBalanceAdjustment> Adjustments,
    AccountReconciliation? Receipt);

// Runs the Application decision against a coherent snapshot, saving receipt/effect atomically.
// A repeated request returns its persisted receipt; Infrastructure owns locks/retries, not arithmetic.
public interface IAccountReconciliationRepository
{
    Task<ReconcileAccountResult> ExecuteAsync(Guid userId, Guid accountId, Guid requestId,
        Func<ReconciliationSnapshot, ReconcileAccountResult> decide, CancellationToken cancellationToken);
}

public interface IAccountBalanceAdjustmentRepository
{
    Task<IReadOnlyList<AccountBalanceAdjustment>> GetAllAsync(Guid userId, CancellationToken cancellationToken);
}
