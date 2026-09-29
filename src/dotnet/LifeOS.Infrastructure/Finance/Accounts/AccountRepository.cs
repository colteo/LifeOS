using LifeOS.Application.Finance.Accounts;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Infrastructure.Persistence;

namespace LifeOS.Infrastructure.Finance.Accounts;

internal sealed class AccountRepository : IAccountRepository
{
    private readonly LifeOSDbContext _dbContext;

    public AccountRepository(LifeOSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(Account account, CancellationToken cancellationToken)
    {
        _dbContext.Accounts.Add(account);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
