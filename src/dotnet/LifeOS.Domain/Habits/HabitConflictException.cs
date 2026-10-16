namespace LifeOS.Domain.Habits;

// A business rule of a habit rejected the operation in the habit's current state (HTTP 409). Code is a
// stable, client-readable identifier (HAB-001 §8); invalid input is an ArgumentException instead (400).
// The optional inner exception keeps the store's error (e.g. a unique index that rejected a concurrent write).
public sealed class HabitConflictException(string code, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public const string Archived = "habit_archived";
    public const string InactiveOnDate = "habit_inactive_on_date";
    public const string CompletionDerived = "habit_completion_derived";

    // HAB-001 §5.1 rule 12 / §8 (B4): at most one active water habit per user; a water habit needs a
    // current Hydration target.
    public const string HydrationHabitExists = "hydration_habit_exists";
    public const string HydrationTargetRequired = "hydration_target_required";

    public string Code { get; } = code;
}
