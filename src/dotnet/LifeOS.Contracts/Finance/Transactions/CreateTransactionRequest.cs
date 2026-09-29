namespace LifeOS.Contracts.Finance.Transactions;

// No currency: it is derived from the account(s) by the API.
public sealed record CreateTransactionRequest(
    string Type,
    decimal Amount,
    Guid? AccountId,
    Guid? SourceAccountId,
    Guid? DestinationAccountId,
    Guid? CategoryId,
    DateTimeOffset? OccurredAtUtc,
    string? Note);
