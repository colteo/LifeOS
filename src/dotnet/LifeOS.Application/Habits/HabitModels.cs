using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Domain.Habits;
using LifeOS.Domain.Nutrition.Hydration;

namespace LifeOS.Application.Habits;

public enum HabitResultStatus
{
    Ok,
    Invalid,
    NotFound,
    Conflict
}

// The outcome of a habit use case. Invalid carries the request field and a readable message; Conflict
// a stable code (HAB-001 §8) and a readable message.
public sealed record HabitResult<T>(HabitResultStatus Status, T? Value = default, string? Field = null, string? Message = null, string? Code = null)
{
    public static HabitResult<T> Ok(T value) => new(HabitResultStatus.Ok, value);

    public static HabitResult<T> NotFound() => new(HabitResultStatus.NotFound);

    public static HabitResult<T> Invalid(string field, string message) => new(HabitResultStatus.Invalid, Field: field, Message: message);

    public static HabitResult<T> Invalid(ArgumentException exception) =>
        Invalid(exception.ParamName ?? "request", HabitErrors.Message(exception));

    public static HabitResult<T> Conflict(string code, string message) => new(HabitResultStatus.Conflict, Code: code, Message: message);

    public static HabitResult<T> Conflict(HabitConflictException exception) => Conflict(exception.Code, exception.Message);
}

// Requested frequency: Weekdays needs weekdays, TimesPerWeek needs timesPerWeek.
public sealed record HabitFrequencyInput(HabitFrequencyKind Kind, IReadOnlyList<DayOfWeek>? Weekdays, int? TimesPerWeek)
{
    public HabitFrequency ToFrequency() => Kind switch
    {
        HabitFrequencyKind.Daily => HabitFrequency.Daily,
        HabitFrequencyKind.Weekdays => HabitFrequency.OnWeekdays(WeekdaySet.Of(Weekdays ?? [])),
        HabitFrequencyKind.TimesPerWeek => TimesPerWeek is { } times
            ? HabitFrequency.PerWeek(times)
            : throw new ArgumentException("Choose how many times per week.", "frequency.timesPerWeek"),
        _ => throw new ArgumentException("Choose every day, specific days or times per week.", "frequency.kind")
    };
}

// One date of a habit, with the stored check-in's note (Excused notes).
public sealed record HabitDayEntry(DateOnly Date, HabitDayStatus Status, string? Note);

// A habit with its statistics as of today and the days of the last five ISO weeks (RecentFrom is the
// Monday four weeks before the current week's). Hydration: today's water day for a Hydration habit, read
// through from HYD-001 (never stored by Habits); null for manual habits.
public sealed record HabitDetail(Habit Habit, HabitProgress Progress, DateOnly RecentFrom, IReadOnlyList<HabitDayEntry> RecentDays,
    HydrationDay? Hydration = null);

// The status of one date after a check-in change, with the habit's updated statistics (Hydration: that
// date's water day for a Hydration habit).
public sealed record HabitDayResult(Habit Habit, HabitDayEntry Day, HabitProgress Progress, HydrationDay? Hydration = null);

public sealed record HabitHistory(Habit Habit, DateOnly From, DateOnly To, IReadOnlyList<HabitDayEntry> Days);

// Where the Today list shows a habit. A TimesPerWeek habit is "to do" every day until its week is met,
// even when the day itself is not scheduled (HAB-001 §8).
public enum HabitListGroup
{
    ToDo,
    Done,
    NotToday
}

// CurrentStreak is as of today; Status, Note, Week and Hydration (water habits) are for the viewed date.
public sealed record HabitListItem(Habit Habit, HabitDayStatus Status, string? Note, HabitListGroup Group, int CurrentStreak,
    HabitStreakUnit StreakUnit, HabitWeekProgress? Week, HydrationDay? Hydration = null);

// Active habits started on or before Date (creation order); Upcoming: active habits starting later.
public sealed record HabitsForDay(DateOnly Date, DateOnly Today, IReadOnlyList<HabitListItem> Items, IReadOnlyList<Habit> Upcoming, int ArchivedCount);

// HAB-001 §6.5 (B4): a Hydration habit's water days, read through IHydrationDayReader (the only Hydration
// surface Habits uses) on every read. Nothing about water is stored or cached by Habits.
internal static class HabitWater
{
    public static readonly IReadOnlyDictionary<DateOnly, HydrationDay> None = new Dictionary<DateOnly, HydrationDay>();

    // from..to by date; empty when the range is empty.
    public static async Task<IReadOnlyDictionary<DateOnly, HydrationDay>> DaysAsync(IHydrationDayReader reader, Guid userId, DateOnly from,
        DateOnly to, CancellationToken cancellationToken) =>
        to < from ? None : (await reader.GetDaysAsync(userId, from, to, cancellationToken)).ToDictionary(day => day.Date);

    // The calculator's input: the dates that have a target, with whether it was met (HYD-001 decides).
    public static IReadOnlyDictionary<DateOnly, bool> TargetMet(IReadOnlyDictionary<DateOnly, HydrationDay> days) =>
        days.Values.Where(day => day.TargetMl is not null).ToDictionary(day => day.Date, day => day.IsTargetMet);
}

internal static class HabitErrors
{
    // ArgumentException appends " (Parameter '...')" to the message.
    public static string Message(ArgumentException exception)
    {
        var index = exception.Message.IndexOf(" (Parameter '", StringComparison.Ordinal);

        return index < 0 ? exception.Message : exception.Message[..index];
    }
}
