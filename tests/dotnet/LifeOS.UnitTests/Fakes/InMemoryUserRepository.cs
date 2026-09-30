using LifeOS.Application.Users;
using LifeOS.Domain.Users;

namespace LifeOS.UnitTests.Fakes;

internal sealed class InMemoryUserRepository : IUserRepository
{
    public List<User> Users { get; } = [];

    public List<ExternalIdentity> Identities { get; } = [];

    // Records every update, so tests can prove the handler persisted the change.
    public List<ExternalIdentity> UpdatedIdentities { get; } = [];

    // Ordinal (case-sensitive) match, like the PostgreSQL unique index.
    public Task<ExternalIdentity?> GetExternalIdentityAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(Identities.SingleOrDefault(identity =>
            string.Equals(identity.Provider, provider, StringComparison.Ordinal)
            && string.Equals(identity.Subject, subject, StringComparison.Ordinal)));
    }

    public Task AddAsync(User user, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        Users.Add(user);
        Identities.Add(identity);

        return Task.CompletedTask;
    }

    public Task UpdateExternalIdentityAsync(ExternalIdentity identity, CancellationToken cancellationToken)
    {
        UpdatedIdentities.Add(identity);

        return Task.CompletedTask;
    }
}
