using LifeOS.Application.Finance.Accounts;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Finance.Accounts;

internal sealed class AccountRepository : IAccountRepository
{
    private readonly LifeOSDbContext _dbContext;

    public AccountRepository(LifeOSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(Account account, OpeningBalance? openingBalance, CancellationToken cancellationToken)
    {
        _dbContext.Accounts.Add(account);

        if (openingBalance is not null)
        {
            _dbContext.OpeningBalances.Add(openingBalance);
        }

        // One save: the account and its opening balance are created together or not at all.
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<Account?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        return await _dbContext.Accounts
            .AsNoTracking()
            .SingleOrDefaultAsync(account => account.UserId == userId && account.Id == id, cancellationToken);
    }

    public async Task<bool> AnyAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Accounts
            .AnyAsync(account => account.UserId == userId, cancellationToken);
    }

    public async Task<IReadOnlyList<Account>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Accounts
            .AsNoTracking()
            .Where(account => account.UserId == userId)
            .ToListAsync(cancellationToken);
    }
}
