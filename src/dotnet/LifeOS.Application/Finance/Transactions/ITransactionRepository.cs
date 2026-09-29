using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Transactions;

public interface ITransactionRepository
{
    Task AddAsync(Transaction transaction, CancellationToken cancellationToken);
}
