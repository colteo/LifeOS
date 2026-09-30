using LifeOS.Application.Finance.Accounts;
using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.UnitTests.Fakes;

// Every read filters by userId; one opening balance per account, like the unique index.
internal sealed class InMemoryOpeningBalanceRepository : IOpeningBalanceRepository
{
    private readonly Lock _lock = new();

    public List<OpeningBalance> OpeningBalances { get; } = [];

    // Runs just before TryAddAsync checks for an existing one, to simulate a concurrent insert.
    public Action? BeforeAdd { get; set; }

    public Task<OpeningBalance?> GetByAccountIdAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(OpeningBalances.SingleOrDefault(openingBalance =>
                openingBalance.UserId == userId && openingBalance.AccountId == accountId));
        }
    }

    public Task<IReadOnlyList<OpeningBalance>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<OpeningBalance>>(
                OpeningBalances.Where(openingBalance => openingBalance.UserId == userId).ToList());
        }
    }

    public Task<bool> TryAddAsync(OpeningBalance openingBalance, CancellationToken cancellationToken)
    {
        BeforeAdd?.Invoke();

        lock (_lock)
        {
            if (OpeningBalances.Any(existing => existing.AccountId == openingBalance.AccountId))
            {
                return Task.FromResult(false);
            }

            OpeningBalances.Add(openingBalance);

            return Task.FromResult(true);
        }
    }
}
