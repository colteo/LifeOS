using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.Application.Finance.Accounts.CreateAccount;

public sealed record CreateAccountCommand(string Name, AccountType AccountType, string Currency);
