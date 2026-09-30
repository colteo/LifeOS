using LifeOS.Application.Users;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LifeOS.Infrastructure.Users;

internal sealed class UserRepository : IUserRepository
{
    private readonly LifeOSDbContext _dbContext;

    public UserRepository(LifeOSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<User?> GetByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == userId, cancellationToken);
    }

    public async Task<ExternalIdentity?> GetExternalIdentityAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken)
    {
        return await _dbContext.ExternalIdentities
            .AsNoTracking()
            .SingleOrDefaultAsync(
                identity => identity.Provider == provider && identity.Subject == subject,
                cancellationToken);
    }

    public async Task<bool> TryAddAsync(User user, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        var userEntry = _dbContext.Users.Add(user);
        var identityEntry = _dbContext.ExternalIdentities.Add(identity);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException exception) when (IsDuplicateExternalIdentity(exception))
        {
            // Nothing was saved. Detach the failed inserts so later saves in this scope don't retry them.
            userEntry.State = EntityState.Detached;
            identityEntry.State = EntityState.Detached;

            return false;
        }
    }

    public async Task UpdateExternalIdentityAsync(ExternalIdentity identity, CancellationToken cancellationToken)
    {
        _dbContext.ExternalIdentities.Update(identity);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TryUpdateOnboardingAsync(
        User user,
        OnboardingStatus expectedStatus,
        CancellationToken cancellationToken)
    {
        // Conditional update: only one concurrent onboarding transition from expectedStatus succeeds.
        var updated = await _dbContext.Users
            .Where(stored => stored.Id == user.Id && stored.OnboardingStatus == expectedStatus)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(stored => stored.OnboardingStatus, user.OnboardingStatus)
                    .SetProperty(stored => stored.DefaultCurrency, user.DefaultCurrency)
                    .SetProperty(stored => stored.StarterCategoriesInitializedAtUtc, user.StarterCategoriesInitializedAtUtc)
                    .SetProperty(stored => stored.OnboardingCompletedAtUtc, user.OnboardingCompletedAtUtc),
                cancellationToken);

        return updated == 1;
    }

    // Only the (provider, subject) identity key; any other unique violation still propagates.
    private static bool IsDuplicateExternalIdentity(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ExternalIdentityConfiguration.ProviderSubjectIndexName
        };
}
