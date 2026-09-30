using LifeOS.Application.Finance.Accounts;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LifeOS.Infrastructure.Finance.Accounts;

internal sealed class OpeningBalanceRepository : IOpeningBalanceRepository
{
    private readonly LifeOSDbContext _dbContext;

    public OpeningBalanceRepository(LifeOSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<OpeningBalance?> GetByAccountIdAsync(Guid userId, Guid accountId, CancellationToken cancellationToken)
    {
        return await _dbContext.OpeningBalances
            .AsNoTracking()
            .SingleOrDefaultAsync(
                openingBalance => openingBalance.UserId == userId && openingBalance.AccountId == accountId,
                cancellationToken);
    }

    public async Task<IReadOnlyList<OpeningBalance>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.OpeningBalances
            .AsNoTracking()
            .Where(openingBalance => openingBalance.UserId == userId)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> TryAddAsync(OpeningBalance openingBalance, CancellationToken cancellationToken)
    {
        var entry = _dbContext.OpeningBalances.Add(openingBalance);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException exception) when (IsDuplicateForAccount(exception) || IsAccountGone(exception))
        {
            // Nothing was saved; detach so a later save in this scope does not retry it.
            entry.State = EntityState.Detached;

            return false;
        }
    }

    // Only the one-per-account index and the account reference (the account was deleted after it was
    // looked up); any other violation still propagates.
    private static bool IsDuplicateForAccount(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: OpeningBalanceConfiguration.AccountIndexName
        };

    private static bool IsAccountGone(DbUpdateException exception) =>
        PostgresErrors.IsForeignKeyViolation(exception, OpeningBalanceConfiguration.AccountForeignKeyName);
}
