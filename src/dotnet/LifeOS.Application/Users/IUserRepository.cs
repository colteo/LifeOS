using LifeOS.Domain.Users;

namespace LifeOS.Application.Users;

public interface IUserRepository
{
    // Exact match on the normalized (provider, subject) identity key.
    Task<ExternalIdentity?> GetExternalIdentityAsync(string provider, string subject, CancellationToken cancellationToken);

    // Persists the user and its first identity together, in a single save.
    Task AddAsync(User user, ExternalIdentity identity, CancellationToken cancellationToken);

    Task UpdateExternalIdentityAsync(ExternalIdentity identity, CancellationToken cancellationToken);
}
