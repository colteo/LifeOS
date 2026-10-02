using LifeOS.Application.Finance.Accounts.ReconcileAccount;
using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.UnitTests.Fakes;

internal sealed class InMemoryAccountReconciliationRepository(InMemoryAccountRepository accounts,
    InMemoryOpeningBalanceRepository openings, InMemoryTransactionRepository transactions)
    : IAccountReconciliationRepository, IAccountBalanceAdjustmentRepository
{
    private readonly SemaphoreSlim _gate = new(1);
    public List<AccountReconciliation> Receipts { get; } = [];
    public List<AccountBalanceAdjustment> Adjustments { get; } = [];

    public async Task<ReconcileAccountResult> ExecuteAsync(Guid userId, Guid accountId, Guid requestId,
        Func<ReconciliationSnapshot, ReconcileAccountResult> decide, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var account = await accounts.GetByIdAsync(userId, accountId, cancellationToken);
            var opening = await openings.GetByAccountIdAsync(userId, accountId, cancellationToken);
            var movements = transactions.Transactions.Where(t => t.UserId == userId).ToList();
            var adjustments = Adjustments.Where(a => a.UserId == userId && a.AccountId == accountId).ToList();
            var receipt = Receipts.SingleOrDefault(r => r.UserId == userId && r.AccountId == accountId && r.RequestId == requestId);
            var result = decide(new(account, opening, movements, adjustments, receipt));
            if (result.Status == ReconcileAccountStatus.Ok && !result.IsReplay)
            {
                Receipts.Add(result.Receipt!);
                if (result.Adjustment is { } adjustment) Adjustments.Add(adjustment);
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AccountBalanceAdjustment>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return Adjustments.Where(a => a.UserId == userId).ToList(); }
        finally { _gate.Release(); }
    }
}
