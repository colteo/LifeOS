namespace LifeOS.Contracts.Finance.Accounts;

// Balance at AtUtc, in the account's currency; null when AtUtc is before the opening balance.
public sealed record AccountBalanceResponse(
    Guid AccountId,
    string Currency,
    decimal? Balance,
    DateTimeOffset AtUtc,
    OpeningBalanceResponse? OpeningBalance);
