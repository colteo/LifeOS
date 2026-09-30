namespace LifeOS.Contracts.Finance.Accounts;

public sealed record OpeningBalanceResponse(Guid AccountId, decimal Amount, DateTimeOffset AsOfUtc);
