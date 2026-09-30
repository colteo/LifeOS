using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Transactions;

public interface ITransactionRepository
{
    Task AddAsync(Transaction transaction, CancellationToken cancellationToken);

    // Transactions owned by userId with fromUtc <= OccurredAtUtc < toUtc.
    Task<IReadOnlyList<Transaction>> GetByOccurredRangeAsync(
        Guid userId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);
}
