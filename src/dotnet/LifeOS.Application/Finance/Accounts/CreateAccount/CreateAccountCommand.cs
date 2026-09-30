using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.Application.Finance.Accounts.CreateAccount;

// OpeningBalance is optional; when given it is created together with the account (ADR-007).
public sealed record CreateAccountCommand(
    string Name,
    AccountType AccountType,
    string Currency,
    OpeningBalanceInput? OpeningBalance = null);
