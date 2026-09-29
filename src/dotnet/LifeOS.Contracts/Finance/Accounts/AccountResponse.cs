namespace LifeOS.Contracts.Finance.Accounts;

public sealed record AccountResponse(
    Guid Id,
    string Name,
    string Type,
    string Currency,
    DateTimeOffset CreatedAtUtc);
