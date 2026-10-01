namespace LifeOS.Application.Finance.Transactions.UpdateTransaction;

public enum UpdateTransactionStatus
{
    Updated,
    Invalid,

    // Field null: the transaction itself (missing or another user's). Otherwise a referenced
    // account or category.
    NotFound
}

public sealed record UpdateTransactionResult(
    UpdateTransactionStatus Status,
    TransactionSummary? Transaction,
    string? Field,
    string? Message)
{
    public const string TransactionNotFoundMessage = "This transaction no longer exists.";

    public static UpdateTransactionResult Updated(TransactionSummary transaction) =>
        new(UpdateTransactionStatus.Updated, transaction, null, null);

    public static UpdateTransactionResult Invalid(string field, string message) =>
        new(UpdateTransactionStatus.Invalid, null, field, message);

    public static UpdateTransactionResult NotFound(string? field, string message) =>
        new(UpdateTransactionStatus.NotFound, null, field, message);

    public static UpdateTransactionResult TransactionNotFound() => NotFound(null, TransactionNotFoundMessage);
}
