namespace LifeOS.Application.Finance.Transactions.GetTransactions;

public enum GetTransactionsStatus
{
    Ok,
    Invalid
}

public sealed record GetTransactionsResult(
    GetTransactionsStatus Status,
    IReadOnlyList<TransactionSummary> Transactions,
    string? Field,
    string? Message)
{
    public static GetTransactionsResult Ok(IReadOnlyList<TransactionSummary> transactions) =>
        new(GetTransactionsStatus.Ok, transactions, null, null);

    public static GetTransactionsResult Invalid(string field, string message) =>
        new(GetTransactionsStatus.Invalid, [], field, message);
}
