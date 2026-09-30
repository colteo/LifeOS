namespace LifeOS.Contracts.Finance.Accounts;

// The account's balance at an exact instant. Amount is signed: negative for a debt (e.g. a credit card).
public sealed record OpeningBalanceRequest(decimal Amount, DateTimeOffset? AsOfUtc);
