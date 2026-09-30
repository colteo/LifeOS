using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.Application.Finance.Accounts;

public interface IAccountRepository
{
    Task AddAsync(Account account, CancellationToken cancellationToken);

    // Only accounts owned by userId; another user's account is indistinguishable from a missing one.
    Task<Account?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Account>> GetAllAsync(Guid userId, CancellationToken cancellationToken);
}
