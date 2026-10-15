using System.Reflection;
using LifeOS.Application.Reminders;
using LifeOS.Domain.Reminders;

namespace LifeOS.UnitTests.Fakes;

// Every operation filters by userId, like the EF Core repository; ownership tests depend on it. Reminders
// are stored and returned as copies, so a caller's unsaved changes never leak into the stored state.
internal sealed class InMemoryReminderRepository : IReminderRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Lock _lock = new();

    public List<Reminder> Reminders { get; } = [];

    public Task<int> CountOpenAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Reminders.Count(reminder => reminder.UserId == userId && reminder.Status != ReminderStatus.Completed));
        }
    }

    public Task AddAsync(Reminder reminder, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Reminders.Add(Clone(reminder));
        }

        return Task.CompletedTask;
    }

    public Task<Reminder?> GetAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Find(userId, reminderId) is { } reminder ? Clone(reminder) : null);
        }
    }

    public Task<IReadOnlyList<Reminder>> GetAllAsync(Guid userId, DateTimeOffset completedSince, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Reminder>>(Reminders
                .Where(reminder => reminder.UserId == userId
                    && (reminder.Status != ReminderStatus.Completed || reminder.UpdatedAtUtc >= completedSince || reminder.SnoozeUntilUtc is not null))
                .OrderBy(reminder => reminder.CreatedAtUtc)
                .ThenBy(reminder => reminder.Id)
                .Select(Clone)
                .ToList());
        }
    }

    public Task<bool> SaveAsync(Reminder reminder, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = Reminders.FindIndex(stored => stored.UserId == reminder.UserId && stored.Id == reminder.Id);

            if (index < 0)
            {
                return Task.FromResult(false);
            }

            Reminders[index] = Clone(reminder);
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Reminders.RemoveAll(reminder => reminder.UserId == userId && reminder.Id == reminderId) > 0);
        }
    }

    // The stored reminder (not a copy), for assertions and test setup.
    public Reminder? Find(Guid userId, Guid reminderId) =>
        Reminders.SingleOrDefault(reminder => reminder.UserId == userId && reminder.Id == reminderId);

    public static Reminder Clone(Reminder reminder) => (Reminder)CloneMethod.Invoke(reminder, null)!;
}
