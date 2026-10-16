namespace LifeOS.Domain.Reminders;

// A business rule of a reminder rejected the operation in its current state (HTTP 409). Code is a
// stable, client-readable identifier (REM-001 §9); invalid input is an ArgumentException instead (400).
public sealed class ReminderConflictException(string code, string message) : InvalidOperationException(message)
{
    public const string Completed = "reminder_completed";
    public const string Paused = "reminder_paused";

    public string Code { get; } = code;
}
