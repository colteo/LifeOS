using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Transactions.CreateTransaction;

// Currency is intentionally absent: it is derived from the account(s).
public sealed record CreateTransactionCommand(
    TransactionType TransactionType,
    decimal Amount,
    Guid? AccountId,
    Guid? SourceAccountId,
    Guid? DestinationAccountId,
    Guid? CategoryId,
    DateTimeOffset OccurredAtUtc,
    string? Note);
