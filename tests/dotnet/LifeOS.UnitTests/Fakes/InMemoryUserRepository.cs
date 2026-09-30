using System.Reflection;
using LifeOS.Application.Users;
using LifeOS.Domain.Users;

namespace LifeOS.UnitTests.Fakes;

internal sealed class InMemoryUserRepository : IUserRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Lock _lock = new();

    // Stored rows. GetByIdAsync returns detached copies, like the EF Core repository, so a
    // handler's in-memory changes are only "stored" when it persists them.
    public List<User> Users { get; } = [];

    public List<ExternalIdentity> Identities { get; } = [];

    // Records every update, so tests can prove the handler persisted the change.
    public List<ExternalIdentity> UpdatedIdentities { get; } = [];

    // Runs just before TryAddAsync checks the identity key, to simulate a concurrent sign-in
    // that inserts the same (provider, subject) first.
    public Action? BeforeAdd { get; set; }

    // Runs just before TryUpdateOnboardingAsync checks the stored status, to simulate a concurrent
    // onboarding request.
    public Action? BeforeUpdateOnboarding { get; set; }

    public int GetExternalIdentityCalls { get; private set; }

    public int OnboardingUpdates { get; private set; }

    public Task<User?> GetByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var user = Users.SingleOrDefault(user => user.Id == userId);

            return Task.FromResult(user is null ? null : Clone(user));
        }
    }

    // The stored row, for assertions.
    public User Stored(Guid userId)
    {
        lock (_lock)
        {
            return Users.Single(user => user.Id == userId);
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

    public Task<bool> TryUpdateOnboardingAsync(User user, OnboardingStatus expectedStatus, CancellationToken cancellationToken)
    {
        BeforeUpdateOnboarding?.Invoke();

        lock (_lock)
        {
            var index = Users.FindIndex(stored => stored.Id == user.Id);

            // Same condition as the conditional UPDATE.
            if (index < 0 || Users[index].OnboardingStatus != expectedStatus)
            {
                return Task.FromResult(false);
            }

            Users[index] = Clone(user);
            OnboardingUpdates++;

            return Task.FromResult(true);
        }
    }

    private ExternalIdentity? Find(string provider, string subject) =>
        Identities.SingleOrDefault(identity =>
            string.Equals(identity.Provider, provider, StringComparison.Ordinal)
            && string.Equals(identity.Subject, subject, StringComparison.Ordinal));

    private static User Clone(User user) => (User)CloneMethod.Invoke(user, null)!;
}
