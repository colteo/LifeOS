using LifeOS.Domain.Reminders;

namespace LifeOS.UnitTests.Reminders;

// REM-001 §5/§13: the ReminderSchedule value object. Wednesday 14 Oct 2026 is "today".
public class ReminderScheduleTests
{
    private static readonly TimeOnly At0830 = new(8, 30);

    private static DateTime Local(int month, int day, int hour, int minute) => new(2026, month, day, hour, minute, 0, DateTimeKind.Unspecified);

    // ---- Shape ----

    [Fact]
    public void Factories_BuildEachShape()
    {
        Assert.Equal((ReminderScheduleKind.Once, (DateOnly?)new DateOnly(2026, 10, 20), (int?)null),
            Shape(ReminderSchedule.Once(new DateOnly(2026, 10, 20), new TimeOnly(10, 0))));
        Assert.Equal((ReminderScheduleKind.Daily, (DateOnly?)null, (int?)null), Shape(ReminderSchedule.Daily(At0830)));

        var weekly = ReminderSchedule.Weekly([DayOfWeek.Thursday, DayOfWeek.Monday, DayOfWeek.Monday], new TimeOnly(19, 0));
        Assert.Equal((ReminderScheduleKind.Weekly, (DateOnly?)null, (int?)(1 | 8)), Shape(weekly));
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Thursday], weekly.Weekdays);

        var all = ReminderSchedule.Weekly(ReminderSchedule.Week, At0830);
        Assert.Equal(ReminderSchedule.AllWeekdaysMask, all.WeekdayMask);
        Assert.Equal(64, ReminderSchedule.Weekly([DayOfWeek.Sunday], At0830).WeekdayMask);
    }

    [Fact]
    public void Shape_IsValidated()
    {
        Assert.Equal("schedule.weekdays", Assert.Throws<ArgumentException>(() => ReminderSchedule.Weekly([], At0830)).ParamName);
        Assert.Equal("schedule.weekdays", Assert.Throws<ArgumentOutOfRangeException>(() => ReminderSchedule.Weekly([(DayOfWeek)9], At0830)).ParamName);
        Assert.Equal("schedule.time", Assert.Throws<ArgumentException>(() => ReminderSchedule.Daily(new TimeOnly(8, 30, 15))).ParamName);
        Assert.Equal("schedule.date", Assert.Throws<ArgumentException>(() => ReminderSchedule.FromParts(ReminderScheduleKind.Once, null, null, At0830)).ParamName);
        Assert.Equal("schedule.date", Assert.Throws<ArgumentException>(() =>
            ReminderSchedule.FromParts(ReminderScheduleKind.Daily, new DateOnly(2026, 10, 20), null, At0830)).ParamName);
        Assert.Equal("schedule.weekdays", Assert.Throws<ArgumentException>(() => ReminderSchedule.FromParts(ReminderScheduleKind.Daily, null, 3, At0830)).ParamName);
        Assert.Equal("schedule.weekdays", Assert.Throws<ArgumentException>(() => ReminderSchedule.FromParts(ReminderScheduleKind.Weekly, null, 128, At0830)).ParamName);
        Assert.Equal("schedule.kind", Assert.Throws<ArgumentOutOfRangeException>(() => ReminderSchedule.FromParts((ReminderScheduleKind)7, null, null, At0830)).ParamName);
        Assert.Equal("schedule.date", Assert.Throws<ArgumentOutOfRangeException>(() => ReminderSchedule.Once(DateOnly.MaxValue, At0830)).ParamName);
        Assert.Equal("schedule.date", Assert.Throws<ArgumentOutOfRangeException>(() => ReminderSchedule.Once(DateOnly.MinValue, At0830)).ParamName);
    }

    [Fact]
    public void EqualSchedules_AreEqual() =>
        Assert.Equal(ReminderSchedule.Weekly([DayOfWeek.Monday], At0830), ReminderSchedule.FromParts(ReminderScheduleKind.Weekly, null, 1, At0830));

    // ---- FirstAfter ----

    [Theory]
    [InlineData(10, 14, 8, 29, 10, 14, 8, 30)]   // later today
    [InlineData(10, 14, 8, 30, 10, 15, 8, 30)]   // strictly after: exactly now is not next
    [InlineData(10, 14, 23, 59, 10, 15, 8, 30)]
    [InlineData(12, 31, 9, 0, 1, 1, 8, 30)]      // year end (2027)
    [InlineData(2, 28, 9, 0, 3, 1, 8, 30)]       // month end
    public void Daily_FirstAfter(int month, int day, int hour, int minute, int nextMonth, int nextDay, int nextHour, int nextMinute)
    {
        var next = ReminderSchedule.Daily(At0830).FirstAfter(Local(month, day, hour, minute))!.Value;

        Assert.Equal((nextMonth, nextDay, nextHour, nextMinute), (next.Month, next.Day, next.Hour, next.Minute));
        Assert.Equal(DateTimeKind.Unspecified, next.Kind);
    }

    [Fact]
    public void Weekly_FirstAfter_WrapsTheWeek()
    {
        var monThu = ReminderSchedule.Weekly([DayOfWeek.Monday, DayOfWeek.Thursday], new TimeOnly(19, 0));

        Assert.Equal(Local(10, 15, 19, 0), monThu.FirstAfter(Local(10, 14, 20, 0)));     // Wed → Thu
        Assert.Equal(Local(10, 19, 19, 0), monThu.FirstAfter(Local(10, 15, 19, 0)));     // Thu 19:00 → Mon
        Assert.Equal(Local(10, 15, 19, 0), monThu.FirstAfter(Local(10, 15, 18, 59)));
        Assert.Equal(new DateTime(2027, 1, 4, 19, 0, 0), monThu.FirstAfter(Local(12, 31, 23, 0)));

        var wednesdays = ReminderSchedule.Weekly([DayOfWeek.Wednesday], new TimeOnly(7, 0));
        Assert.Equal(Local(10, 21, 7, 0), wednesdays.FirstAfter(Local(10, 14, 7, 0)));   // a full week later
    }

    [Fact]
    public void Once_FirstAfter_IsTheDateOrNothing()
    {
        var once = ReminderSchedule.Once(new DateOnly(2026, 10, 20), new TimeOnly(10, 0));

        Assert.Equal(Local(10, 20, 10, 0), once.FirstAfter(Local(10, 20, 9, 59)));
        Assert.Null(once.FirstAfter(Local(10, 20, 10, 0)));
        Assert.Null(once.FirstAfter(Local(11, 1, 0, 0)));
    }

    [Fact]
    public void FirstAfter_NearTheEndOfTime_ReturnsNothingRatherThanOverflowing() =>
        Assert.Null(ReminderSchedule.Daily(At0830).FirstAfter(new DateTime(9999, 12, 31, 12, 0, 0)));

    // ---- LatestAtOrBefore ----

    [Fact]
    public void LatestAtOrBefore_ForEachKind()
    {
        var daily = ReminderSchedule.Daily(At0830);
        Assert.Equal(Local(10, 14, 8, 30), daily.LatestAtOrBefore(Local(10, 14, 8, 30)));
        Assert.Equal(Local(10, 13, 8, 30), daily.LatestAtOrBefore(Local(10, 14, 8, 29)));
        Assert.Equal(new DateTime(2025, 12, 31, 8, 30, 0), daily.LatestAtOrBefore(new DateTime(2026, 1, 1, 0, 0, 0)));

        var monThu = ReminderSchedule.Weekly([DayOfWeek.Monday, DayOfWeek.Thursday], new TimeOnly(19, 0));
        Assert.Equal(Local(10, 12, 19, 0), monThu.LatestAtOrBefore(Local(10, 14, 12, 0)));   // Wed → Mon
        Assert.Equal(Local(10, 15, 19, 0), monThu.LatestAtOrBefore(Local(10, 18, 23, 0)));   // Sun → Thu

        var wednesdays = ReminderSchedule.Weekly([DayOfWeek.Wednesday], new TimeOnly(7, 0));
        Assert.Equal(Local(10, 7, 7, 0), wednesdays.LatestAtOrBefore(Local(10, 14, 6, 59)));

        var once = ReminderSchedule.Once(new DateOnly(2026, 10, 20), new TimeOnly(10, 0));
        Assert.Null(once.LatestAtOrBefore(Local(10, 20, 9, 59)));
        Assert.Equal(Local(10, 20, 10, 0), once.LatestAtOrBefore(Local(10, 30, 0, 0)));
    }

    // ---- Includes ----

    [Fact]
    public void Includes_OnlyExactOccurrences()
    {
        var daily = ReminderSchedule.Daily(At0830);
        Assert.True(daily.Includes(Local(10, 14, 8, 30)));
        Assert.False(daily.Includes(Local(10, 14, 8, 31)));

        var monThu = ReminderSchedule.Weekly([DayOfWeek.Monday, DayOfWeek.Thursday], new TimeOnly(19, 0));
        Assert.True(monThu.Includes(Local(10, 15, 19, 0)));
        Assert.False(monThu.Includes(Local(10, 14, 19, 0)));

        var once = ReminderSchedule.Once(new DateOnly(2026, 10, 20), new TimeOnly(10, 0));
        Assert.True(once.Includes(Local(10, 20, 10, 0)));
        Assert.False(once.Includes(Local(10, 21, 10, 0)));
    }

    [Fact]
    public void ToMinute_DropsSecondsAndKeepsUnspecifiedKind()
    {
        var truncated = ReminderSchedule.ToMinute(new DateTime(2026, 10, 14, 8, 30, 59, 999, DateTimeKind.Utc));

        Assert.Equal((Local(10, 14, 8, 30), DateTimeKind.Unspecified), (truncated, truncated.Kind));
    }

    private static (ReminderScheduleKind, DateOnly?, int?) Shape(ReminderSchedule schedule) => (schedule.Kind, schedule.Date, schedule.WeekdayMask);
}
