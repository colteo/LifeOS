using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Transactions;

public interface ITransactionRepository
{
    // Returns false, persisting nothing, when a referenced account or category no longer exists
    // (deleted concurrently after the caller looked it up). The database enforces the references.
    Task<bool> TryAddAsync(Transaction transaction, CancellationToken cancellationToken);

    // Only transactions owned by userId; another user's transaction is indistinguishable from a
    // missing one.
    Task<Transaction?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    // Stores the editable fields (amount, currency, references, note, occurred time) on the row with
    // the same id and owner. Type, owner and creation time never change.
    Task<TransactionUpdateOutcome> TryUpdateAsync(Transaction transaction, CancellationToken cancellationToken);

    // Deletes one transaction of userId; nothing references a transaction. False when there is none.
    Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    // Whether any transaction of userId references the account: as its account, or as the source or
    // destination of a transfer.
    Task<bool> AnyReferencingAccountAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);

    // Whether any transaction of userId references the category.
    Task<bool> AnyReferencingCategoryAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken);

    // The newest `limit` transactions owned by userId: OccurredAtUtc, CreatedAtUtc, Id, all descending.
    Task<IReadOnlyList<Transaction>> GetRecentAsync(Guid userId, int limit, CancellationToken cancellationToken);

    // Transactions owned by userId with OccurredAtUtc < beforeUtc (the input of derived balances).
    Task<IReadOnlyList<Transaction>> GetOccurredBeforeAsync(
        Guid userId,
        DateTimeOffset beforeUtc,
        CancellationToken cancellationToken);

    // Transactions owned by userId with fromUtc <= OccurredAtUtc < toUtc.
    Task<IReadOnlyList<Transaction>> GetByOccurredRangeAsync(
        Guid userId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);
}

public enum TransactionUpdateOutcome
{
    Updated,

    // No transaction with this id for this user (e.g. deleted concurrently).
    NotFound,

    // A referenced account or category no longer exists (deleted concurrently); nothing was written.
    ReferenceMissing
}
