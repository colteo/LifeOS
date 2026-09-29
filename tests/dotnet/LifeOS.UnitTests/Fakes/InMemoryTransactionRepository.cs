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
}
