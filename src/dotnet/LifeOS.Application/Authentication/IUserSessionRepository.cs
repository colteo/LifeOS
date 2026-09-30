using LifeOS.Domain.Users;

namespace LifeOS.Application.Authentication;

public interface IUserSessionRepository
{
    Task AddAsync(UserSession session, CancellationToken cancellationToken);

    Task<UserSession?> GetByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken);

    // Atomically marks `rotated` as revoked/replaced and inserts `replacement`, but only if `rotated`
    // is still unrevoked in storage. Returns false, persisting nothing, when another request rotated
    // or revoked it first.
    Task<bool> TryRotateAsync(UserSession rotated, UserSession replacement, CancellationToken cancellationToken);

    // Revokes every still-unrevoked session of the family.
    Task RevokeFamilyAsync(Guid familyId, DateTimeOffset revokedAtUtc, CancellationToken cancellationToken);
}
