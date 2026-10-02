using System.Reflection;
using LifeOS.Application.Finance.Accounts;
using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.UnitTests.Fakes;

// Every read filters by userId, like the EF Core repository; ownership tests depend on it.
// Reads return detached copies, like AsNoTracking, so a handler's in-memory changes are only
// "stored" when it persists them.
internal sealed class InMemoryAccountRepository : IAccountRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Lock _lock = new();

    // Opening balances saved together with their account (shared with the opening-balance fake).
    public InMemoryAccountRepository(InMemoryOpeningBalanceRepository? openingBalances = null)
    {
        OpeningBalances = openingBalances ?? new InMemoryOpeningBalanceRepository();

        // Like the (account_id, user_id) foreign key: an opening balance needs its account.
        OpeningBalances.AccountExists = (userId, accountId) =>
        {
            lock (_lock)
            {
                return Accounts.Any(account => account.UserId == userId && account.Id == accountId);
            }
        };
    }

    public List<Account> Accounts { get; } = [];

    public InMemoryOpeningBalanceRepository OpeningBalances { get; }

    // Runs just before TryUpdateAsync / DeleteAsync touch the stored rows, to simulate a
    // concurrent request.
    public Action? BeforeWrite { get; set; }

    // When set, DeleteAsync reports this outcome without deleting, as if a restricting foreign key
    // (a concurrent transaction or opening balance) had rejected the delete.
    public AccountDeleteOutcome? RejectDeleteWith { get; set; }

    public Task AddAsync(Account account, OpeningBalance? openingBalance, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Accounts.Add(account);

            if (openingBalance is not null)
            {
                OpeningBalances.OpeningBalances.Add(openingBalance);
            }
        }

        return Task.CompletedTask;
    }

    public Task<Account?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var account = Accounts.SingleOrDefault(account => account.UserId == userId && account.Id == id);

            return Task.FromResult(account is null ? null : Clone(account));
        }
    }

    public Task<bool> AnyAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Accounts.Any(account => account.UserId == userId));
        }
    }

    public Task<IReadOnlyList<Account>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Account>>(
                Accounts.Where(account => account.UserId == userId).Select(Clone).ToList());
        }
    }

    // Replaces the stored row with the same id and owner, like the conditional UPDATE.
    public Task<bool> TryUpdateAsync(Account account, CancellationToken cancellationToken)
    {
        BeforeWrite?.Invoke();

        lock (_lock)
        {
            var index = Accounts.FindIndex(stored => stored.Id == account.Id && stored.UserId == account.UserId);

            if (index < 0)
            {
                return Task.FromResult(false);
            }

            Accounts[index] = Clone(account);

            return Task.FromResult(true);
        }
    }

    public Func<Guid, Guid, bool>? ReconciliationsExist { get; set; }

    public Task<AccountDeleteOutcome> DeleteAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        BeforeWrite?.Invoke();

        lock (_lock)
        {
            if (!Accounts.Any(account => account.UserId == userId && account.Id == accountId))
            {
                return Task.FromResult(AccountDeleteOutcome.NotFound);
            }

            if (RejectDeleteWith is { } outcome)
            {
                return Task.FromResult(outcome);
            }

            if (ReconciliationsExist?.Invoke(userId, accountId) == true)
                return Task.FromResult(AccountDeleteOutcome.HasReconciliations);

            Accounts.RemoveAll(account => account.UserId == userId && account.Id == accountId);
            OpeningBalances.OpeningBalances.RemoveAll(openingBalance =>
                openingBalance.UserId == userId && openingBalance.AccountId == accountId);

            return Task.FromResult(AccountDeleteOutcome.Deleted);
        }
    }

    // The stored row, for assertions.
    public Account Stored(Guid accountId)
    {
        lock (_lock)
        {
            return Accounts.Single(account => account.Id == accountId);
        }
    }

    private static Account Clone(Account account) => (Account)CloneMethod.Invoke(account, null)!;
}
