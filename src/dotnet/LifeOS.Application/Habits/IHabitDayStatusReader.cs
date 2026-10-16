using LifeOS.Domain.Habits;

namespace LifeOS.Application.Habits;

// HAB-001 §7.1: the read port other modules (REM-001 HabitIncomplete reminders) use for one habit's
// status on one local date. Read-only and computed on every call by the same calculator as the Habits
// screens, so the rule exists once. Null when the user has no such habit (another user's included).
public interface IHabitDayStatusReader
{
    Task<HabitDayStatusSnapshot?> GetStatusAsync(Guid userId, Guid habitId, DateOnly date, CancellationToken cancellationToken);
}

// IsActive: the habit is not archived. Status: the date's derived status, judged as if the date were
// "today" (Pending until it is Done, Excused or not scheduled). WeekDone/WeekTarget: the ISO week's goal
// of a times-per-week habit up to the date; null otherwise.
public sealed record HabitDayStatusSnapshot(Guid HabitId, bool IsActive, HabitDayStatus Status, int? WeekDone, int? WeekTarget);
