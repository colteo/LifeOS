using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Transactions;

public interface ITransactionRepository
{
    // Returns false, persisting nothing, when a referenced account or category no longer exists
    // (deleted concurrently after the caller looked it up). The database enforces the references.
    Task<bool> TryAddAsync(Transaction transaction, CancellationToken cancellationToken);

    // Whether any transaction of userId references the account: as its account, or as the source or
    // destination of a transfer.
    Task<bool> AnyReferencingAccountAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);

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
