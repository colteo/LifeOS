using LifeOS.Application.Authentication;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Users;

internal sealed class UserSessionRepository : IUserSessionRepository
{
    private readonly LifeOSDbContext _dbContext;

    public UserSessionRepository(LifeOSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(UserSession session, CancellationToken cancellationToken)
    {
        _dbContext.UserSessions.Add(session);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<UserSession?> GetByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken)
    {
        return await _dbContext.UserSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(session => session.RefreshTokenHash == refreshTokenHash, cancellationToken);
    }

    public async Task<bool> TryRotateAsync(UserSession rotated, UserSession replacement, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Conditional update: only one concurrent refresh of the same session can succeed.
        var updated = await _dbContext.UserSessions
            .Where(session => session.Id == rotated.Id && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(session => session.RevokedAtUtc, rotated.RevokedAtUtc)
                    .SetProperty(session => session.ReplacedBySessionId, rotated.ReplacedBySessionId),
                cancellationToken);

        if (updated == 0)
        {
            return false;
        }

        _dbContext.UserSessions.Add(replacement);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    public async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset revokedAtUtc, CancellationToken cancellationToken)
    {
        await _dbContext.UserSessions
            .Where(session => session.FamilyId == familyId && session.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(session => session.RevokedAtUtc, revokedAtUtc),
                cancellationToken);
    }
}
