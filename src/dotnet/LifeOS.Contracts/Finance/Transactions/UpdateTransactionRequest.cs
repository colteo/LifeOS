namespace LifeOS.Contracts.Finance.Transactions;

// Edits an existing transaction. There is no type: the stored type is immutable. Exactly one branch:
// AccountTransaction for an Income or Expense (the stored type decides which category type is
// required), Transfer for a transfer. No currency: it is derived from the account(s).
// OccurredAtUtc is required: send the stored instant unchanged to keep it exactly.
public sealed record UpdateTransactionRequest(
    decimal Amount,
    DateTimeOffset? OccurredAtUtc,
    string? Note,
    AccountTransactionUpdate? AccountTransaction,
    TransferUpdate? Transfer);

public sealed record AccountTransactionUpdate(Guid? AccountId, Guid? CategoryId);

public sealed record TransferUpdate(Guid? SourceAccountId, Guid? DestinationAccountId);
