namespace LifeOS.Domain.Reminders;

// REM-001 §5 (D-1). Custom: the user's own reminder (title, optional note, once/daily/weekly).
// HabitIncomplete / HydrationGoal (R5): conditional nudges, daily or weekly only, sent only while one
// habit is still open / today's water target isn't reached; they carry no text of their own.
public enum ReminderKind
{
    Custom,
    HabitIncomplete,
    HydrationGoal
}

public enum ReminderScheduleKind
{
    Once,
    Daily,
    Weekly
}

// Active: NextDueLocal is set. Paused: nothing is discovered (snooze included). Completed: a one-time
// reminder with no further occurrence; it can still be snoozed or edited to a new date.
public enum ReminderStatus
{
    Active,
    Paused,
    Completed
}

// The outcome of the latest occurrence. Notified: the logical notification was produced (devices may
// still be offline). Missed: the occurrence was found after its lateness and skipped (D-14). Disabled:
// the reminder type's switch ("Personal", "Habit" or "Water reminders") was off when the occurrence ran.
// Suppressed (conditional kinds, D-6): the condition was already met, so nothing was sent.
public enum ReminderOutcome
{
    Notified,
    Missed,
    Disabled,
    Suppressed
}
