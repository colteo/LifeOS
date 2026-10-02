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

    // Stores the account's editable fields (name and type) on the row with the same id and owner.
    // Returns false, writing nothing, when that row no longer exists (e.g. deleted concurrently).
    Task<bool> TryUpdateAsync(Account account, CancellationToken cancellationToken);

    // Deletes the account and its opening balance, if any, atomically. The database restricts
    // deleting an account that transactions reference.
    Task<AccountDeleteOutcome> DeleteAsync(Guid userId, Guid accountId, CancellationToken cancellationToken);
}

public enum AccountDeleteOutcome
{
    Deleted,

    // No account with this id for this user.
    NotFound,

    // Transactions reference the account; nothing was deleted.
    HasTransactions,

    // Immutable reconciliation audit records prevent deletion, including zero-difference receipts.
    HasReconciliations,

    // An opening balance was added concurrently while deleting; nothing was deleted. Retryable.
    Changed
}
