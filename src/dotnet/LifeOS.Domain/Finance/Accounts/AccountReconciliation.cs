using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Domain.Finance.Accounts;

// Immutable audit result of one current-balance reconciliation, including no-op requests.
public sealed class AccountReconciliation
{
    private AccountReconciliation(Guid id, Guid userId, Guid accountId, Guid requestId,
        decimal previousBalance, decimal observedBalance, DateTimeOffset effectiveAtUtc,
        DateTimeOffset createdAtUtc, string? note)
    {
        Id = id; UserId = userId; AccountId = accountId; RequestId = requestId;
        PreviousBalance = previousBalance; ObservedBalance = observedBalance;
        EffectiveAtUtc = effectiveAtUtc; CreatedAtUtc = createdAtUtc; Note = note;
    }

    public Guid Id { get; }
    public Guid UserId { get; }
    public Guid AccountId { get; }
    public Guid RequestId { get; }
    public decimal PreviousBalance { get; }
    public decimal ObservedBalance { get; }
    public DateTimeOffset EffectiveAtUtc { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string? Note { get; }
    public decimal AdjustmentAmount => ObservedBalance - PreviousBalance;

    public static AccountReconciliation Create(Account account, Guid requestId, decimal previousBalance,
        decimal observedBalance, DateTimeOffset now, string? note)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (requestId == Guid.Empty) throw new ArgumentException("A valid request id is required.", nameof(requestId));
        ValidateMoney(observedBalance, nameof(observedBalance));
        ValidateMoney(observedBalance - previousBalance, "adjustmentAmount");
        var utc = OpeningBalance.NormalizeAsOf(now);
        return new(Guid.CreateVersion7(), account.UserId, account.Id, requestId,
            previousBalance, observedBalance, utc, utc, NormalizeNote(note));
    }

    public static string? NormalizeNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    public static void ValidateMoney(decimal amount, string parameterName)
    {
        if (amount < -Transaction.MaxAmount || amount > Transaction.MaxAmount)
            throw new ArgumentOutOfRangeException(parameterName, "The balance or adjustment exceeds the supported monetary range.");
        if (decimal.Round(amount, Transaction.MaxDecimalPlaces) != amount)
            throw new ArgumentException("Monetary values must have at most 4 decimal places.", parameterName);
    }
}
