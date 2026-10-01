using System.Reflection;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.UnitTests.Fakes;

// Every read filters by userId, like the EF Core repository; ownership tests depend on it.
internal sealed class InMemoryTransactionRepository : ITransactionRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

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

    // A detached copy, like AsNoTracking: changes are only stored through TryUpdateAsync.
    public Task<Transaction?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var transaction = Transactions.SingleOrDefault(stored => stored.UserId == userId && stored.Id == id);

            return Task.FromResult(transaction is null ? null : Clone(transaction));
        }
    }

    // Runs just before TryUpdateAsync / DeleteAsync touch the stored rows, to simulate a concurrent
    // request (e.g. deleting the transaction or a referenced account).
    public Action? BeforeWrite { get; set; }

    // Replaces the stored row with the same id and owner, like the conditional UPDATE; references are
    // checked like the foreign keys when ReferencesExist is set.
    public Task<TransactionUpdateOutcome> TryUpdateAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        BeforeWrite?.Invoke();

        lock (_lock)
        {
            var index = Transactions.FindIndex(stored => stored.Id == transaction.Id && stored.UserId == transaction.UserId);

            if (index < 0)
            {
                return Task.FromResult(TransactionUpdateOutcome.NotFound);
            }

            if (ReferencesExist is not null && !ReferencesExist(transaction))
            {
                return Task.FromResult(TransactionUpdateOutcome.ReferenceMissing);
            }

            Transactions[index] = Clone(transaction);

            return Task.FromResult(TransactionUpdateOutcome.Updated);
        }
    }

    public Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        BeforeWrite?.Invoke();

        lock (_lock)
        {
            return Task.FromResult(Transactions.RemoveAll(stored => stored.UserId == userId && stored.Id == id) == 1);
        }
    }

    // The stored row, for assertions.
    public Transaction Stored(Guid id)
    {
        lock (_lock)
        {
            return Transactions.Single(stored => stored.Id == id);
        }
    }

    private static Transaction Clone(Transaction transaction) => (Transaction)CloneMethod.Invoke(transaction, null)!;

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
