namespace LifeOS.Contracts.Finance.Accounts;

public sealed record CreateAccountRequest(string Name, string Type, string Currency);
