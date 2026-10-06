using LifeOS.Application.Notifications;
using LifeOS.Domain.Notifications;

namespace LifeOS.UnitTests.Fakes;

// Same contract as the PostgreSQL repository: no saved row = the defaults; saving needs an existing
// user. TimeZones stands in for users.time_zone_id.
internal sealed class InMemoryNotificationPreferencesRepository : INotificationPreferencesRepository
{
    private readonly Lock _lock = new();

    public Dictionary<Guid, NotificationPreferences> Saved { get; } = [];

    public Dictionary<Guid, string?> TimeZones { get; } = [];

    // Users that exist; null = every user exists.
    public HashSet<Guid>? ExistingUsers { get; set; }

    public Task<NotificationPreferences> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Saved.TryGetValue(userId, out var saved) ? saved : NotificationPreferences.Default(userId));
        }
    }

    public Task<bool> SetAsync(NotificationPreferences preferences, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (ExistingUsers is not null && !ExistingUsers.Contains(preferences.UserId))
            {
                return Task.FromResult(false);
            }

            Saved[preferences.UserId] = preferences;
            return Task.FromResult(true);
        }
    }

    public Task<QuietHoursContext?> GetQuietHoursContextAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (ExistingUsers is not null && !ExistingUsers.Contains(userId))
            {
                return Task.FromResult<QuietHoursContext?>(null);
            }

            var quietHours = Saved.TryGetValue(userId, out var saved) ? saved.QuietHours : QuietHours.Default;

            return Task.FromResult<QuietHoursContext?>(new QuietHoursContext(TimeZones.GetValueOrDefault(userId), quietHours));
        }
    }

    public void Set(Guid userId, bool recurring = true, bool planned = true, TimeOnly? start = null, TimeOnly? end = null)
    {
        lock (_lock)
        {
            Saved[userId] = NotificationPreferences.Create(
                userId, recurring, planned, QuietHours.Create(start ?? QuietHours.DefaultStart, end ?? QuietHours.DefaultEnd), DateTimeOffset.UnixEpoch);
        }
    }
}
