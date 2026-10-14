namespace LifeOS.Domain.Habits;

// The stored status of one habit on one local date (HAB-001 §5): Done or Excused, with an optional note.
// Identity = HabitId + Date; saving again replaces status and note (idempotent upsert). Created only
// through Habit.CheckIn, which enforces the date rules.
public sealed class HabitCheckIn
{
    public const int NoteMaxLength = 280;

    private HabitCheckIn() { }

    internal HabitCheckIn(Guid habitId, Guid userId, DateOnly date, HabitCheckInStatus status, string? note, DateTimeOffset now)
    {
        HabitId = habitId;
        UserId = userId;
        Date = date;
        Status = status;
        Note = note;
        CreatedAtUtc = now;
        UpdatedAtUtc = now;
    }

    public Guid HabitId { get; private set; }

    public Guid UserId { get; private set; }

    public DateOnly Date { get; private set; }

    public HabitCheckInStatus Status { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    // Blank is "no note". Outer whitespace is trimmed; longer notes are rejected, never cut.
    internal static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var text = note.Trim();

        if (text.Length > NoteMaxLength)
        {
            throw new ArgumentException($"The note must be at most {NoteMaxLength} characters.", nameof(note));
        }

        return text;
    }
}
