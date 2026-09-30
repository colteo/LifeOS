using LifeOS.Application.Finance.Transactions;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.UnitTests.Fakes;

internal sealed class InMemoryTransactionRepository : ITransactionRepository
{
    public List<Transaction> Transactions { get; } = [];

    public Task AddAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        Transactions.Add(transaction);

        return Task.CompletedTask;
    }

    // Same half-open range as the EF Core repository. Deliberately unordered, so tests prove
    // that the handler applies the ordering rule itself.
    public Task<IReadOnlyList<Transaction>> GetByOccurredRangeAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<Transaction>>(Transactions
            .Where(transaction => transaction.OccurredAtUtc >= fromUtc && transaction.OccurredAtUtc < toUtc)
            .ToList());
    }
}
