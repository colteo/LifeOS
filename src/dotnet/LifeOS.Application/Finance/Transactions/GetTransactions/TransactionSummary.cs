using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Transactions.GetTransactions;

public sealed record TransactionSummary(
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
