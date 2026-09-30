using LifeOS.Domain.Users;

namespace LifeOS.Application.Users;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid userId, CancellationToken cancellationToken);

    // Exact match on the normalized (provider, subject) identity key.
    Task<ExternalIdentity?> GetExternalIdentityAsync(string provider, string subject, CancellationToken cancellationToken);

    // Persists the user and its first identity together, in a single save.
    // Returns false, persisting nothing, when the (provider, subject) identity already exists
    // (e.g. a concurrent first sign-in created it first).
    Task<bool> TryAddAsync(User user, ExternalIdentity identity, CancellationToken cancellationToken);

    Task UpdateExternalIdentityAsync(ExternalIdentity identity, CancellationToken cancellationToken);
}
