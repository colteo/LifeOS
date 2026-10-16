using LifeOS.Domain.Reminders;

namespace LifeOS.Application.Reminders;

// REM-001 §8: the user's reminders. Every method is scoped to userId: another user's reminder is
// indistinguishable from a missing one.
public interface IReminderRepository
{
    // Reminders that are not Completed (the D-13 limit).
    Task<int> CountOpenAsync(Guid userId, CancellationToken cancellationToken);

    Task AddAsync(Reminder reminder, CancellationToken cancellationToken);

    // Tracked: the caller may change it and pass it to SaveAsync.
    Task<Reminder?> GetAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken);

    // Every Active and Paused reminder, and the Completed ones updated at or after completedSince or
    // with a pending snooze.
    Task<IReadOnlyList<Reminder>> GetAllAsync(Guid userId, DateTimeOffset completedSince, CancellationToken cancellationToken);

    // Saves a reminder obtained from GetAsync. False when it no longer exists.
    Task<bool> SaveAsync(Reminder reminder, CancellationToken cancellationToken);

    // Hard delete. False when the user has no such reminder.
    Task<bool> DeleteAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken);
}
