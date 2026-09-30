using LifeOS.Application.Finance.Transactions;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.UnitTests.Fakes;

// Every read filters by userId, like the EF Core repository; ownership tests depend on it.
internal sealed class InMemoryTransactionRepository : ITransactionRepository
{
    private readonly Lock _lock = new();

    public List<Transaction> Transactions { get; } = [];

    public Task AddAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Transactions.Add(transaction);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Transaction>> GetOccurredBeforeAsync(
        Guid userId,
        DateTimeOffset beforeUtc,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Transaction>>(Transactions
                .Where(transaction => transaction.UserId == userId && transaction.OccurredAtUtc < beforeUtc)
                .ToList());
        }
    }

    // Same owner filter and half-open range as the EF Core repository. Deliberately unordered,
    // so tests prove that the handler applies the ordering rule itself.
    public Task<IReadOnlyList<Transaction>> GetByOccurredRangeAsync(
        Guid userId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Transaction>>(Transactions
                .Where(transaction => transaction.UserId == userId
                    && transaction.OccurredAtUtc >= fromUtc
                    && transaction.OccurredAtUtc < toUtc)
                .ToList());
        }
    }
}
