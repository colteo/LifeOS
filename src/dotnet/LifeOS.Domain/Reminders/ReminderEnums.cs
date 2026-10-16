namespace LifeOS.Domain.Reminders;

// REM-001 §5. v1 ships the user's own reminders only; the conditional kinds (HabitIncomplete,
// HydrationGoal) arrive with R5.
public enum ReminderKind
{
    Custom
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
// the user's "Personal reminders" switch was off when the occurrence ran.
public enum ReminderOutcome
{
    Notified,
    Missed,
    Disabled
}
