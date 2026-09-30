using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Transactions;

public interface ITransactionRepository
{
    Task AddAsync(Transaction transaction, CancellationToken cancellationToken);

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
