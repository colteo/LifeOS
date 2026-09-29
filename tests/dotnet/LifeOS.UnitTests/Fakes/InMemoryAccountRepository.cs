using LifeOS.Application.Finance.Accounts;
using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.UnitTests.Fakes;

internal sealed class InMemoryAccountRepository : IAccountRepository
{
    public List<Account> Accounts { get; } = [];

    public Task AddAsync(Account account, CancellationToken cancellationToken)
    {
        Accounts.Add(account);

        return Task.CompletedTask;
    }
}
