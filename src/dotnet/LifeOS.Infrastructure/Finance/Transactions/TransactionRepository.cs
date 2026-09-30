using LifeOS.Application.Finance.Transactions;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Finance.Transactions;

internal sealed class TransactionRepository : ITransactionRepository
{
    // The referenced account or category was deleted after the caller looked it up: its row is gone,
    // so the reference foreign key rejects the insert. Only these four constraints are recovered.
    private static readonly IReadOnlyCollection<string> ReferenceForeignKeyNames =
        [.. TransactionConfiguration.AccountForeignKeyNames, TransactionConfiguration.CategoryForeignKeyName];

    private readonly LifeOSDbContext _dbContext;

    public TransactionRepository(LifeOSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> TryAddAsync(Transaction transaction, CancellationToken cancellationToken)
    {
        var entry = _dbContext.Transactions.Add(transaction);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsForeignKeyViolation(exception, ReferenceForeignKeyNames))
        {
            // Nothing was saved; detach so a later save in this scope does not retry it.
            entry.State = EntityState.Detached;

            return false;
        }
    }

    public async Task<bool> AnyReferencingAccountAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        return await _dbContext.Transactions
            .AnyAsync(
                transaction => transaction.UserId == userId
                    && (transaction.AccountId == accountId
                        || transaction.SourceAccountId == accountId
                        || transaction.DestinationAccountId == accountId),
                cancellationToken);
    }

    public async Task<bool> AnyReferencingCategoryAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken)
    {
        return await _dbContext.Transactions
            .AnyAsync(transaction => transaction.UserId == userId && transaction.CategoryId == categoryId, cancellationToken);
    }

    public async Task<IReadOnlyList<Transaction>> GetRecentAsync(Guid userId, int limit, CancellationToken cancellationToken)
    {
        return await _dbContext.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.UserId == userId)
            .OrderByDescending(transaction => transaction.OccurredAtUtc)
            .ThenByDescending(transaction => transaction.CreatedAtUtc)
            .ThenByDescending(transaction => transaction.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Transaction>> GetOccurredBeforeAsync(
        Guid userId,
        DateTimeOffset beforeUtc,
        CancellationToken cancellationToken)
    {
        return await _dbContext.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.UserId == userId && transaction.OccurredAtUtc < beforeUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Transaction>> GetByOccurredRangeAsync(
        Guid userId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        return await _dbContext.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.UserId == userId
                && transaction.OccurredAtUtc >= fromUtc
                && transaction.OccurredAtUtc < toUtc)
            .OrderByDescending(transaction => transaction.OccurredAtUtc)
            .ThenByDescending(transaction => transaction.CreatedAtUtc)
            .ThenByDescending(transaction => transaction.Id)
            .ToListAsync(cancellationToken);
    }
}
