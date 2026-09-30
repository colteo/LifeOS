using LifeOS.Application.Finance.Transactions;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.UnitTests.Fakes;

// Every read filters by userId, like the EF Core repository; ownership tests depend on it.
internal sealed class InMemoryTransactionRepository : ITransactionRepository
{
    private readonly Lock _lock = new();

    public List<Transaction> Transactions { get; } = [];

    // Runs just before TryAddAsync checks the references, to simulate a concurrent delete.
    public Action? BeforeAdd { get; set; }

    // When set, like the reference foreign keys: TryAddAsync stores nothing and returns false unless
    // every referenced account and category still exists.
    public Func<Transaction, bool>? ReferencesExist { get; set; }

    public Task<bool> TryAddAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        BeforeAdd?.Invoke();

        lock (_lock)
        {
            if (ReferencesExist is not null && !ReferencesExist(transaction))
            {
                return Task.FromResult(false);
            }

            Transactions.Add(transaction);

            return Task.FromResult(true);
        }
    }

    public Task<bool> AnyReferencingAccountAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Transactions.Any(transaction => transaction.UserId == userId
                && (transaction.AccountId == accountId
                    || transaction.SourceAccountId == accountId
                    || transaction.DestinationAccountId == accountId)));
        }
    }

    public Task<bool> AnyReferencingCategoryAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Transactions.Any(transaction =>
                transaction.UserId == userId && transaction.CategoryId == categoryId));
        }
    }

    // Same owner filter, ordering and limit as the EF Core repository.
    public Task<IReadOnlyList<Transaction>> GetRecentAsync(Guid userId, int limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Transaction>>(Transactions
                .Where(transaction => transaction.UserId == userId)
                .OrderByDescending(transaction => transaction.OccurredAtUtc)
                .ThenByDescending(transaction => transaction.CreatedAtUtc)
                .ThenByDescending(transaction => transaction.Id)
                .Take(limit)
                .ToList());
        }
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
