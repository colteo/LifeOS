using LifeOS.Application.Finance.Accounts;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
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

    // Conditional on id and owner; only the editable columns are written (never the currency).
    public async Task<bool> TryUpdateAsync(Account account, CancellationToken cancellationToken)
    {
        var updated = await _dbContext.Accounts
            .Where(stored => stored.Id == account.Id && stored.UserId == account.UserId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(stored => stored.Name, account.Name)
                    .SetProperty(stored => stored.AccountType, account.AccountType),
                cancellationToken);

        return updated == 1;
    }

    // One database transaction: the opening balance, then the account. On any failure nothing is
    // deleted. The restricting foreign keys decide the expected failures (23001, or 23503 before
    // PostgreSQL 18), recognized by name:
    //   - a transaction references the account (e.g. created after the caller's check);
    //   - an opening balance was inserted after this transaction deleted "the" opening balance.
    // Any other error still propagates.
    public async Task<AccountDeleteOutcome> DeleteAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            await _dbContext.OpeningBalances
                .Where(openingBalance => openingBalance.AccountId == accountId && openingBalance.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);

            var deleted = await _dbContext.Accounts
                .Where(account => account.Id == accountId && account.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);

            if (deleted == 0)
            {
                // Missing or another user's: disposing the transaction rolls it back.
                return AccountDeleteOutcome.NotFound;
            }

            await transaction.CommitAsync(cancellationToken);

            return AccountDeleteOutcome.Deleted;
        }
        catch (Exception exception) when (PostgresErrors.IsDeleteBlockedByReference(exception, TransactionConfiguration.AccountForeignKeyNames))
        {
            return AccountDeleteOutcome.HasTransactions;
        }
        catch (Exception exception) when (PostgresErrors.IsDeleteBlockedByReference(exception,
            new[] { AccountBalanceAdjustmentConfiguration.AccountForeignKeyName, AccountReconciliationConfiguration.AccountForeignKeyName }))
        {
            return AccountDeleteOutcome.HasReconciliations;
        }
        catch (Exception exception) when (PostgresErrors.IsDeleteBlockedByReference(exception, OpeningBalanceConfiguration.AccountForeignKeyName))
        {
            return AccountDeleteOutcome.Changed;
        }
    }
}
