using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Transactions.CreateTransaction;

public enum CreateTransactionStatus
{
    Created,
    Invalid,
    NotFound
}

public sealed record CreatedTransaction(
    Guid Id,
    TransactionType TransactionType,
    decimal Amount,
    string Currency,
    Guid? AccountId,
    Guid? SourceAccountId,
    Guid? DestinationAccountId,
    Guid? CategoryId,
    string? Note,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset CreatedAtUtc);

public sealed record CreateTransactionResult(
    CreateTransactionStatus Status,
    CreatedTransaction? Transaction,
    string? Field,
    string? Message)
{
    public static CreateTransactionResult Created(CreatedTransaction transaction) =>
        new(CreateTransactionStatus.Created, transaction, null, null);

    public static CreateTransactionResult Invalid(string field, string message) =>
        new(CreateTransactionStatus.Invalid, null, field, message);

    public static CreateTransactionResult NotFound(string field, string message) =>
        new(CreateTransactionStatus.NotFound, null, field, message);
}
