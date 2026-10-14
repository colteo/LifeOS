namespace LifeOS.Domain.Habits;

// A business rule of a habit rejected the operation in the habit's current state (HTTP 409). Code is a
// stable, client-readable identifier (HAB-001 §8); invalid input is an ArgumentException instead (400).
public sealed class HabitConflictException(string code, string message) : InvalidOperationException(message)
{
    public const string Archived = "habit_archived";
    public const string InactiveOnDate = "habit_inactive_on_date";
    public const string CompletionDerived = "habit_completion_derived";

    public string Code { get; } = code;
}
