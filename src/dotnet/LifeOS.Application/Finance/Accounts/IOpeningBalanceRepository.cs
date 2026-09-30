using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.Application.Finance.Accounts;

public interface IOpeningBalanceRepository
{
    // Only opening balances owned by userId.
    Task<OpeningBalance?> GetByAccountIdAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);

    Task<IReadOnlyList<OpeningBalance>> GetAllAsync(Guid userId, CancellationToken cancellationToken);

    // Returns false, persisting nothing, when the account already has an opening balance
    // (e.g. created concurrently). The database enforces one per account.
    Task<bool> TryAddAsync(OpeningBalance openingBalance, CancellationToken cancellationToken);
}
