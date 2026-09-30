namespace LifeOS.Contracts.Finance.Accounts;

// OpeningBalance is optional; when given, the account and its opening balance are created together.
public sealed record CreateAccountRequest(
    string Name,
    string Type,
    string Currency,
    OpeningBalanceRequest? OpeningBalance = null);
