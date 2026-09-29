using LifeOS.Application.Finance.Transactions;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Infrastructure.Persistence;

namespace LifeOS.Infrastructure.Finance.Transactions;

internal sealed class TransactionRepository : ITransactionRepository
{
    private readonly LifeOSDbContext _dbContext;

    public TransactionRepository(LifeOSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        _dbContext.Transactions.Add(transaction);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
