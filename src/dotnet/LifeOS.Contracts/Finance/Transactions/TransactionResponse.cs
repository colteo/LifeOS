namespace LifeOS.Contracts.Finance.Transactions;

public sealed record TransactionResponse(
    Guid Id,
    string Type,
    decimal Amount,
    string Currency,
    Guid? AccountId,
    Guid? SourceAccountId,
    Guid? DestinationAccountId,
    Guid? CategoryId,
    DateTimeOffset OccurredAtUtc,
    string? Note,
    DateTimeOffset CreatedAtUtc);
