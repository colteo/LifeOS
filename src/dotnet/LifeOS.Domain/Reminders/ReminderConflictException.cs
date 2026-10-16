namespace LifeOS.Domain.Reminders;

// A business rule of a reminder rejected the operation in its current state (HTTP 409). Code is a
// stable, client-readable identifier (REM-001 §9); invalid input is an ArgumentException instead (400).
// The optional inner exception keeps the store's error (e.g. a unique index that rejected a concurrent write).
public sealed class ReminderConflictException(string code, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public const string Completed = "reminder_completed";
    public const string Paused = "reminder_paused";

    // D-13: at most one HabitIncomplete reminder per habit and one HydrationGoal reminder per user.
    public const string ExistsForHabit = "reminder_exists_for_habit";
    public const string HydrationExists = "hydration_reminder_exists";

    public string Code { get; } = code;
}
