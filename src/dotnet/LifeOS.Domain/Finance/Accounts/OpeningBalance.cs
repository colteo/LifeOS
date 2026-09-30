using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Domain.Finance.Accounts;

// The declared balance of an account at an exact instant (ADR-007). Optional, at most one per
// account, never a transaction. It has no currency of its own: the account's currency applies.
public sealed class OpeningBalance
{
    // Allowance for the difference between the client clock (where "now" is captured) and the server.
    public static readonly TimeSpan AllowedClockSkew = TimeSpan.FromMinutes(5);

    private OpeningBalance(
        Guid id,
        Guid userId,
        Guid accountId,
        decimal amount,
        DateTimeOffset asOfUtc,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        UserId = userId;
        AccountId = accountId;
        Amount = amount;
        AsOfUtc = asOfUtc;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    public Guid UserId { get; }

    public Guid AccountId { get; }

    // Signed: positive is an asset (money owned), negative a liability (e.g. credit-card debt).
    public decimal Amount { get; }

    // The exact instant at which Amount was the account's balance.
    public DateTimeOffset AsOfUtc { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    // Owner and account come from the account itself.
    public static OpeningBalance Create(
        Account account,
        decimal amount,
        DateTimeOffset asOfUtc,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(account);

        var asOf = NormalizeAsOf(asOfUtc);
        var createdAt = createdAtUtc.ToUniversalTime();

        if (asOf > createdAt + AllowedClockSkew)
        {
            throw new ArgumentOutOfRangeException(nameof(asOfUtc), asOfUtc, "The opening balance cannot be in the future.");
        }

        return new OpeningBalance(
            Guid.CreateVersion7(),
            account.UserId,
            account.Id,
            ValidateAmount(amount),
            asOf,
            createdAt);
    }

    // UTC, truncated to whole microseconds (the precision PostgreSQL stores), so a retried request
    // with the same instant compares equal to the stored value.
    public static DateTimeOffset NormalizeAsOf(DateTimeOffset asOfUtc)
    {
        var utc = asOfUtc.ToUniversalTime();

        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    // Same range and precision as transaction amounts, but signed and zero allowed.
    private static decimal ValidateAmount(decimal amount)
    {
        if (Math.Abs(amount) > Transaction.MaxAmount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                amount,
                $"Amount must be between -{Transaction.MaxAmount} and {Transaction.MaxAmount}.");
        }

        if (decimal.Round(amount, Transaction.MaxDecimalPlaces) != amount)
        {
            throw new ArgumentException(
                $"Amount must have at most {Transaction.MaxDecimalPlaces} decimal places.",
                nameof(amount));
        }

        return amount;
    }
}
