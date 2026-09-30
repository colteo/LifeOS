using LifeOS.Application.Finance.Accounts;
using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.UnitTests.Fakes;

// Every read filters by userId, like the EF Core repository; ownership tests depend on it.
internal sealed class InMemoryAccountRepository : IAccountRepository
{
    private readonly Lock _lock = new();

    public List<Account> Accounts { get; } = [];

    public Task AddAsync(Account account, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Accounts.Add(account);
        }

        return Task.CompletedTask;
    }

    public Task<Account?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Accounts.SingleOrDefault(account => account.UserId == userId && account.Id == id));
        }
    }

    public Task<IReadOnlyList<Account>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Account>>(Accounts.Where(account => account.UserId == userId).ToList());
        }
    }
}
