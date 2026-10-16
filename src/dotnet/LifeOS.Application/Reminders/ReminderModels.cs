using LifeOS.Domain.Reminders;

namespace LifeOS.Application.Reminders;

public enum ReminderResultStatus
{
    Ok,
    Invalid,
    NotFound,
    Conflict
}

// The outcome of a reminder use case. Invalid carries the request field and a readable message;
// Conflict a stable code (REM-001 §9) and a readable message.
public sealed record ReminderResult<T>(ReminderResultStatus Status, T? Value = default, string? Field = null, string? Message = null, string? Code = null)
{
    public static ReminderResult<T> Ok(T value) => new(ReminderResultStatus.Ok, value);

    public static ReminderResult<T> NotFound() => new(ReminderResultStatus.NotFound);

    public static ReminderResult<T> Invalid(string field, string message) => new(ReminderResultStatus.Invalid, Field: field, Message: message);

    public static ReminderResult<T> Invalid(ArgumentException exception) =>
        Invalid(exception.ParamName ?? "request", ReminderErrors.Message(exception));

    public static ReminderResult<T> Conflict(string code, string message) => new(ReminderResultStatus.Conflict, Code: code, Message: message);

    public static ReminderResult<T> Conflict(ReminderConflictException exception) => Conflict(exception.Code, exception.Message);
}

// Requested schedule: Once needs a date, Weekly needs weekdays.
public sealed record ReminderScheduleInput(ReminderScheduleKind Kind, DateOnly? Date, IReadOnlyList<DayOfWeek>? Weekdays, TimeOnly Time)
{
    public ReminderSchedule ToSchedule() => Kind switch
    {
        ReminderScheduleKind.Once => Date is { } date
            ? ReminderSchedule.Once(date, Time)
            : throw new ArgumentException("Choose a date.", "schedule.date"),
        ReminderScheduleKind.Daily => ReminderSchedule.Daily(Time),
        ReminderScheduleKind.Weekly => ReminderSchedule.Weekly(Weekdays ?? [], Time),
        _ => throw new ArgumentException("Choose once, every day or some days.", "schedule.kind")
    };
}

// A reminder as the app shows it. InsideQuietHours: its time falls inside the user's quiet hours, so a
// push is held back until they end (D-9, editor warning; a nudge expires instead). Label: what the list
// shows: the title, or the fixed text of a conditional reminder with its habit's name (§4.1).
public sealed record ReminderView(Reminder Reminder, bool InsideQuietHours, string Label);

// The Reminders screen (§4.1). Upcoming: Active, by next occurrence. Paused: by creation. Completed:
// the last 30 days (and any with a pending snooze), most recent first. TimeZoneKnown: the device reported
// the user's zone (without it nothing is ever due). PushAvailable: the server can send pushes.
public sealed record RemindersOverview(
    bool TimeZoneKnown,
    bool PushAvailable,
    IReadOnlyList<ReminderView> Upcoming,
    IReadOnlyList<ReminderView> Paused,
    IReadOnlyList<ReminderView> Completed);

public static class ReminderLimits
{
    public const string HydrationLabel = "If today's water target isn't reached";

    // The habit's name is the user's own text: inside the app only.
    public static string HabitLabel(string? habitName) =>
        habitName is null ? "If a habit isn't done" : $"If “{habitName}” isn't done";

    // D-13: abuse/cost guard.
    public const int MaxOpenReminders = 50;

    public const int MaxUtcOffsetMinutes = 14 * 60;

    // Completed reminders stay listed this long (§4.1).
    public static readonly TimeSpan CompletedListed = TimeSpan.FromDays(30);

    public const string LimitReachedCode = "reminder_limit_reached";

    // The device's local wall-clock time now, to the minute. Throws
    // ArgumentOutOfRangeException("utcOffsetMinutes") outside ±14 h.
    public static DateTime LocalNow(TimeProvider clock, int utcOffsetMinutes)
    {
        if (utcOffsetMinutes is < -MaxUtcOffsetMinutes or > MaxUtcOffsetMinutes)
        {
            throw new ArgumentOutOfRangeException("utcOffsetMinutes", "UTC offset must be between -840 and 840 minutes.");
        }

        return ReminderSchedule.ToMinute(clock.GetUtcNow().ToOffset(TimeSpan.FromMinutes(utcOffsetMinutes)).DateTime);
    }
}

internal static class ReminderErrors
{
    // ArgumentException appends " (Parameter '...')" to the message.
    public static string Message(ArgumentException exception)
    {
        var index = exception.Message.IndexOf(" (Parameter '", StringComparison.Ordinal);

        return index < 0 ? exception.Message : exception.Message[..index];
    }
}
