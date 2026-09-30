namespace LifeOS.Application.Finance.Transactions.GetRecentTransactions;

public enum GetRecentTransactionsStatus
{
    Ok,
    Invalid
}

public sealed record GetRecentTransactionsResult(
    GetRecentTransactionsStatus Status,
    IReadOnlyList<TransactionSummary> Transactions,
    string? Field,
    string? Message)
{
    public static GetRecentTransactionsResult Ok(IReadOnlyList<TransactionSummary> transactions) =>
        new(GetRecentTransactionsStatus.Ok, transactions, null, null);

    public static GetRecentTransactionsResult Invalid(string field, string message) =>
        new(GetRecentTransactionsStatus.Invalid, [], field, message);
}
