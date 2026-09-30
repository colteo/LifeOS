using LifeOS.Application.Finance.Accounts.SetOpeningBalance;

namespace LifeOS.Application.Finance.Accounts.GetAccountBalances;

public enum GetAccountBalancesStatus
{
    Ok,
    Invalid
}

// Balance is null when AtUtc is before the account's opening balance (not available).
// Currency is the account's currency.
public sealed record AccountBalance(
    Guid AccountId,
    string Currency,
    decimal? Balance,
    DateTimeOffset AtUtc,
    OpeningBalanceSummary? OpeningBalance);

public sealed record GetAccountBalancesResult(
    GetAccountBalancesStatus Status,
    IReadOnlyList<AccountBalance> Balances,
    string? Field,
    string? Message)
{
    public static GetAccountBalancesResult Ok(IReadOnlyList<AccountBalance> balances) =>
        new(GetAccountBalancesStatus.Ok, balances, null, null);

    public static GetAccountBalancesResult Invalid(string field, string message) =>
        new(GetAccountBalancesStatus.Invalid, [], field, message);
}
