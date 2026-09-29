using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.Application.Finance.Accounts.CreateAccount;

public sealed record CreateAccountResult(
    Guid Id,
    string Name,
    AccountType AccountType,
    string Currency,
    DateTimeOffset CreatedAtUtc);
