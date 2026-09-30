using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.Application.Finance.Accounts;

public interface IAccountRepository
{
    // Persists the account and, when given, its opening balance together, in a single save.
    Task AddAsync(Account account, OpeningBalance? openingBalance, CancellationToken cancellationToken);

    // Only accounts owned by userId; another user's account is indistinguishable from a missing one.
    Task<Account?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Account>> GetAllAsync(Guid userId, CancellationToken cancellationToken);

    // Whether userId owns at least one account.
    Task<bool> AnyAsync(Guid userId, CancellationToken cancellationToken);
}
