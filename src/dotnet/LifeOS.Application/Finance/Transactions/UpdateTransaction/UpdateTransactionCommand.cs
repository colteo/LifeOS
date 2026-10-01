namespace LifeOS.Application.Finance.Transactions.UpdateTransaction;

// The editable shape of a transaction. There is no type: the stored type is authoritative and
// immutable. Exactly one branch is expected: AccountTransaction for Income/Expense, Transfer for a
// transfer. Currency is absent: it is derived from the account(s).
public sealed record UpdateTransactionCommand(
    Guid TransactionId,
    decimal Amount,
    DateTimeOffset OccurredAtUtc,
    string? Note,
    AccountTransactionInput? AccountTransaction,
    TransferInput? Transfer);

public sealed record AccountTransactionInput(Guid? AccountId, Guid? CategoryId);

public sealed record TransferInput(Guid? SourceAccountId, Guid? DestinationAccountId);
