namespace LifeOS.Application.Finance.Accounts.SetOpeningBalance;

public sealed record SetOpeningBalanceCommand(Guid AccountId, decimal Amount, DateTimeOffset AsOfUtc);
