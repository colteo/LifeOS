using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.Application.Finance.Accounts.GetAccounts;

public sealed record AccountSummary(
    Guid Id,
    string Name,
    AccountType AccountType,
    string Currency,
    DateTimeOffset CreatedAtUtc);
