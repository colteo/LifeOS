namespace LifeOS.Domain.Reminders;

// REM-001 §5 (D-2): when a reminder occurs, as local wall-clock times in the user's current IANA zone
// (alarm-clock semantics: a Daily 08:30 is 08:30 wherever the phone is). Whole minutes only.
//
// Occurrences are DateTime values with Kind Unspecified: local calendar data, never instants. Turning
// one into an instant (DST gaps and overlaps) is the automation's job (LocalSchedule.ToUtc).
//
// Weekdays are an ISO bitmask: Monday 1, Tuesday 2, ... Sunday 64 (1..127).
public sealed record ReminderSchedule
{
    public const int AllWeekdaysMask = 127;

    // Npgsql represents the DateOnly endpoints as PostgreSQL infinities, not calendar dates; a week of
    // margin also keeps every FirstAfter/LatestAtOrBefore computation inside DateTime's range.
    public static readonly DateOnly MinDate = new(1, 1, 8);
    public static readonly DateOnly MaxDate = new(9999, 12, 23);

    public static readonly IReadOnlyList<DayOfWeek> Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private ReminderSchedule(ReminderScheduleKind kind, DateOnly? date, int? weekdayMask, TimeOnly time)
    {
        Kind = kind;
        Date = date;
        WeekdayMask = weekdayMask;
        Time = time;
    }

    public ReminderScheduleKind Kind { get; }

    // Once only.
    public DateOnly? Date { get; }

    // Weekly only, 1..127.
    public int? WeekdayMask { get; }

    public TimeOnly Time { get; }

    // Monday to Sunday; empty unless Weekly.
    public IReadOnlyList<DayOfWeek> Weekdays => Week.Where(IncludesDay).ToList();

    public static ReminderSchedule Once(DateOnly date, TimeOnly time) => FromParts(ReminderScheduleKind.Once, date, null, time);

    public static ReminderSchedule Daily(TimeOnly time) => FromParts(ReminderScheduleKind.Daily, null, null, time);

    public static ReminderSchedule Weekly(IEnumerable<DayOfWeek> days, TimeOnly time)
    {
        ArgumentNullException.ThrowIfNull(days);

        var mask = 0;

        foreach (var day in days)
        {
            if (!Enum.IsDefined(day))
            {
                throw new ArgumentOutOfRangeException("schedule.weekdays", "Use weekday names, Monday to Sunday.");
            }

            mask |= Bit(day);
        }

        return FromParts(ReminderScheduleKind.Weekly, null, mask, time);
    }

    // The stored shape (persistence): validates exactly like the factories.
    public static ReminderSchedule FromParts(ReminderScheduleKind kind, DateOnly? date, int? weekdayMask, TimeOnly time)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException("schedule.kind", "Choose once, every day or some days.");
        }

        if (!IsWholeMinute(time))
        {
            throw new ArgumentException("Choose a time in whole minutes.", "schedule.time");
        }

        if ((kind == ReminderScheduleKind.Once) != date.HasValue)
        {
            throw new ArgumentException(kind == ReminderScheduleKind.Once ? "Choose a date." : "Only a one-time reminder has a date.", "schedule.date");
        }

        if (date is { } day && (day < MinDate || day > MaxDate))
        {
            throw new ArgumentOutOfRangeException("schedule.date", "Choose a valid date.");
        }

        if (kind == ReminderScheduleKind.Weekly)
        {
            if (weekdayMask is not (> 0 and <= AllWeekdaysMask))
            {
                throw new ArgumentException("Choose at least one day.", "schedule.weekdays");
            }
        }
        else if (weekdayMask is not null)
        {
            throw new ArgumentException("Only a reminder on some days has weekdays.", "schedule.weekdays");
        }

        return new ReminderSchedule(kind, date, weekdayMask, time);
    }

    public bool IncludesDay(DayOfWeek day) => WeekdayMask is { } mask && (mask & Bit(day)) != 0;

    // Whether localDateTime is exactly one of this schedule's occurrences.
    public bool Includes(DateTime localDateTime) =>
        TimeOnly.FromDateTime(localDateTime) == Time
        && Kind switch
        {
            ReminderScheduleKind.Once => DateOnly.FromDateTime(localDateTime) == Date,
            ReminderScheduleKind.Daily => true,
            _ => IncludesDay(localDateTime.DayOfWeek)
        };

    // The first occurrence strictly after localNow, or null (a one-time reminder in the past).
    public DateTime? FirstAfter(DateTime localNow)
    {
        if (Kind == ReminderScheduleKind.Once)
        {
            var at = At(Date!.Value);
            return at > localNow ? at : null;
        }

        var today = DateOnly.FromDateTime(localNow);

        if (today > MaxDate)
        {
            return null;
        }

        for (var days = 0; days <= 7; days++)
        {
            var candidate = At(today.AddDays(days));

            if (candidate > localNow && OccursOn(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // The most recent occurrence at or before localNow, or null (not yet, for a one-time reminder).
    public DateTime? LatestAtOrBefore(DateTime localNow)
    {
        if (Kind == ReminderScheduleKind.Once)
        {
            var at = At(Date!.Value);
            return at <= localNow ? at : null;
        }

        var today = DateOnly.FromDateTime(localNow);

        if (today < MinDate)
        {
            return null;
        }

        for (var days = 0; days <= 7; days++)
        {
            var candidate = At(today.AddDays(-days));

            if (candidate <= localNow && OccursOn(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // Truncates to the minute (Kind Unspecified): the precision of every occurrence.
    public static DateTime ToMinute(DateTime local) =>
        DateTime.SpecifyKind(new DateTime(local.Ticks - local.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Unspecified);

    // ISO weekday number: Monday 1 ... Sunday 7.
    public static int Iso(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    private bool OccursOn(DateTime candidate) => Kind == ReminderScheduleKind.Daily || IncludesDay(candidate.DayOfWeek);

    private DateTime At(DateOnly date) => date.ToDateTime(Time, DateTimeKind.Unspecified);

    private static int Bit(DayOfWeek day) => 1 << (Iso(day) - 1);

    private static bool IsWholeMinute(TimeOnly time) => time.Ticks % TimeSpan.TicksPerMinute == 0;
}
