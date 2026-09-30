namespace LifeOS.Application.Finance.Accounts;

// A declared account balance at an exact instant (ADR-007).
public sealed record OpeningBalanceInput(decimal Amount, DateTimeOffset AsOfUtc);
