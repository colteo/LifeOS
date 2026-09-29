using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.Application.Finance.Accounts;

public interface IAccountRepository
{
    Task AddAsync(Account account, CancellationToken cancellationToken);
}
