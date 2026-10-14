using LifeOS.Domain.Habits;

namespace LifeOS.Application.Habits;

// Habits and their check-ins (HAB-001 §7.1). Every method is scoped to userId: another user's habit is
// indistinguishable from a missing one.
public interface IHabitRepository
{
    Task<int> CountActiveAsync(Guid userId, CancellationToken cancellationToken);

    // Saves the habit and its first schedule version in one transaction.
    Task AddAsync(Habit habit, CancellationToken cancellationToken);

    // With its schedule versions, tracked: the caller may change it and pass it to SaveAsync.
    Task<Habit?> GetAsync(Guid userId, Guid habitId, CancellationToken cancellationToken);

    // Every habit of the user with its schedule versions, in creation order.
    Task<IReadOnlyList<Habit>> GetAllAsync(Guid userId, CancellationToken cancellationToken);

    // Saves a habit obtained from GetAsync (details, status and schedule versions). False when it no
    // longer exists.
    Task<bool> SaveAsync(Habit habit, CancellationToken cancellationToken);

    // Hard delete; the schedule versions and check-ins cascade. False when the user has no such habit.
    Task<bool> DeleteAsync(Guid userId, Guid habitId, CancellationToken cancellationToken);

    // The check-ins of these habits with from <= Date <= to.
    Task<IReadOnlyList<HabitCheckIn>> GetCheckInsAsync(Guid userId, IReadOnlyCollection<Guid> habitIds, DateOnly from, DateOnly to,
        CancellationToken cancellationToken);

    // Inserts the check-in or replaces the status and note of the date's existing one (one row per habit
    // and date, also under concurrency: the last write wins). False when the habit no longer exists.
    Task<bool> UpsertCheckInAsync(HabitCheckIn checkIn, CancellationToken cancellationToken);

    // Idempotent: false when there was nothing to remove.
    Task<bool> RemoveCheckInAsync(Guid userId, Guid habitId, DateOnly date, CancellationToken cancellationToken);
}
