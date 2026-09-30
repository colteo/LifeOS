using LifeOS.Application.Users;
using LifeOS.Domain.Users;

namespace LifeOS.UnitTests.Fakes;

internal sealed class InMemoryUserRepository : IUserRepository
{
    private readonly Lock _lock = new();

    public List<User> Users { get; } = [];

    public List<ExternalIdentity> Identities { get; } = [];

    // Records every update, so tests can prove the handler persisted the change.
    public List<ExternalIdentity> UpdatedIdentities { get; } = [];

    // Runs just before TryAddAsync checks the identity key, to simulate a concurrent sign-in
    // that inserts the same (provider, subject) first.
    public Action? BeforeAdd { get; set; }

    public int GetExternalIdentityCalls { get; private set; }

    public Task<User?> GetByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Users.SingleOrDefault(user => user.Id == userId));
        }
    }

    // Ordinal (case-sensitive) match, like the PostgreSQL unique index.
    public Task<ExternalIdentity?> GetExternalIdentityAsync(
        string provider,
        string subject,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            GetExternalIdentityCalls++;

            return Task.FromResult(Find(provider, subject));
        }
    }

    public Task<bool> TryAddAsync(User user, ExternalIdentity identity, CancellationToken cancellationToken)
    {
        BeforeAdd?.Invoke();

        lock (_lock)
        {
            // Mirrors the unique (provider, subject) constraint: nothing is saved on conflict.
            if (Find(identity.Provider, identity.Subject) is not null)
            {
                return Task.FromResult(false);
            }

            Users.Add(user);
            Identities.Add(identity);

            return Task.FromResult(true);
        }
    }

    public Task UpdateExternalIdentityAsync(ExternalIdentity identity, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            UpdatedIdentities.Add(identity);
        }

        return Task.CompletedTask;
    }

    private ExternalIdentity? Find(string provider, string subject) =>
        Identities.SingleOrDefault(identity =>
            string.Equals(identity.Provider, provider, StringComparison.Ordinal)
            && string.Equals(identity.Subject, subject, StringComparison.Ordinal));
}
