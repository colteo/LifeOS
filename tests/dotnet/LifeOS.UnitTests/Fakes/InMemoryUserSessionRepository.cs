using System.Reflection;
using LifeOS.Application.Authentication;
using LifeOS.Domain.Users;

namespace LifeOS.UnitTests.Fakes;

// Behaves like the EF Core repository: callers get detached copies, so changes a handler makes
// in memory are not "stored" until the repository persists them.
internal sealed class InMemoryUserSessionRepository : IUserSessionRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly PropertyInfo RevokedAtUtcProperty =
        typeof(UserSession).GetProperty(nameof(UserSession.RevokedAtUtc))!;

    private readonly Lock _lock = new();
    private readonly List<UserSession> _sessions = [];

    // Runs just before TryRotateAsync checks the stored row, to simulate a concurrent refresh.
    public Action? BeforeRotate { get; set; }

    // Snapshot of the stored rows.
    public IReadOnlyList<UserSession> Sessions
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Select(Clone).ToList();
            }
        }
    }

    public Task AddAsync(UserSession session, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _sessions.Add(Clone(session));
        }

        return Task.CompletedTask;
    }

    public Task<UserSession?> GetByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var session = _sessions.SingleOrDefault(session => session.RefreshTokenHash == refreshTokenHash);

            return Task.FromResult(session is null ? null : Clone(session));
        }
    }

    public Task<bool> TryRotateAsync(UserSession rotated, UserSession replacement, CancellationToken cancellationToken)
    {
        BeforeRotate?.Invoke();

        lock (_lock)
        {
            var index = _sessions.FindIndex(session => session.Id == rotated.Id);

            // Same condition as the conditional UPDATE: only a still-unrevoked row can be rotated.
            if (index < 0 || _sessions[index].RevokedAtUtc is not null)
            {
                return Task.FromResult(false);
            }

            _sessions[index] = Clone(rotated);
            _sessions.Add(Clone(replacement));

            return Task.FromResult(true);
        }
    }

    public Task RevokeFamilyAsync(Guid familyId, DateTimeOffset revokedAtUtc, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            foreach (var session in _sessions.Where(session => session.FamilyId == familyId && session.RevokedAtUtc is null))
            {
                RevokedAtUtcProperty.SetValue(session, revokedAtUtc);
            }
        }

        return Task.CompletedTask;
    }

    // Simulates a direct database change, e.g. a concurrent request revoking the row.
    public void RevokeStored(Guid sessionId, DateTimeOffset revokedAtUtc)
    {
        lock (_lock)
        {
            RevokedAtUtcProperty.SetValue(_sessions.Single(session => session.Id == sessionId), revokedAtUtc);
        }
    }

    private static UserSession Clone(UserSession session) => (UserSession)CloneMethod.Invoke(session, null)!;
}
