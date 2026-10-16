namespace LifeOS.Domain.Habits;

// HAB-001 §5. Manual: the user checks the habit in. Hydration ("Reach my water target", B4): done is
// derived from Hydration (HYD-001) on every read and never stored; only Excused is stored for it.
public enum HabitCompletionSource
{
    Manual,
    Hydration
}

// A list filter only; the history of a habit is read from its schedule versions.
public enum HabitStatus
{
    Active,
    Archived
}

// Daily and Weekdays are day-based; TimesPerWeek is judged per ISO week; Inactive is an archived period.
public enum HabitFrequencyKind
{
    Daily,
    Weekdays,
    TimesPerWeek,
    Inactive
}

// What a check-in stores for one local date. Undo deletes the check-in.
public enum HabitCheckInStatus
{
    Done,
    Excused
}

// The derived status of one date (HAB-001 §6.1).
public enum HabitDayStatus
{
    Done,
    Excused,
    Missed,
    Pending,
    NotScheduled,
    Inactive
}

public enum HabitStreakUnit
{
    Day,
    Week
}
