namespace LifeOS.Domain.Finance.Accounts;

// A reconciliation effect, not a transaction. Currency is always the owning account's.
public sealed class AccountBalanceAdjustment
{
    private AccountBalanceAdjustment(Guid id, Guid userId, Guid accountId, decimal amount,
        decimal observedBalance, DateTimeOffset effectiveAtUtc, DateTimeOffset createdAtUtc, string? note)
    {
        Id = id; UserId = userId; AccountId = accountId; Amount = amount;
        ObservedBalance = observedBalance; EffectiveAtUtc = effectiveAtUtc;
        CreatedAtUtc = createdAtUtc; Note = note;
    }
    public Guid Id { get; }
    public Guid UserId { get; }
    public Guid AccountId { get; }
    public decimal Amount { get; }
    public decimal ObservedBalance { get; }
    public DateTimeOffset EffectiveAtUtc { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string? Note { get; }

    public static AccountBalanceAdjustment? From(AccountReconciliation reconciliation) =>
        reconciliation.AdjustmentAmount == 0m ? null : new(reconciliation.Id, reconciliation.UserId,
            reconciliation.AccountId, reconciliation.AdjustmentAmount, reconciliation.ObservedBalance,
            reconciliation.EffectiveAtUtc, reconciliation.CreatedAtUtc, reconciliation.Note);
}
