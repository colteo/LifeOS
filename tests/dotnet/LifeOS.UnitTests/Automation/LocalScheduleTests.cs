using LifeOS.Application.Automation;

namespace LifeOS.UnitTests.Automation;

public class LocalScheduleTests
{
    private static TimeZoneInfo Rome => TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");

    [Fact]
    public void ResolveWeekly_WhenDueInsideWindow_ReturnsOccurrence()
    {
        var now = new DateTimeOffset(2026, 10, 4, 18, 3, 0, TimeSpan.Zero);

        var occurrence = LocalSchedule.ResolveWeekly(
            Rome,
            now,
            DayOfWeek.Sunday,
            new TimeOnly(20, 0),
            TimeSpan.FromHours(24));

        Assert.NotNull(occurrence);
        Assert.Equal(new DateOnly(2026, 10, 4), occurrence.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 18, 0, 0, TimeSpan.Zero), occurrence.DueAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 18, 0, 0, TimeSpan.Zero), occurrence.ExpiresAtUtc);
    }

    [Fact]
    public void ResolveWeekly_BeforeDue_ReturnsNone()
    {
        var now = new DateTimeOffset(2026, 10, 4, 17, 59, 59, TimeSpan.Zero);

        Assert.Null(LocalSchedule.ResolveWeekly(
            Rome,
            now,
            DayOfWeek.Sunday,
            new TimeOnly(20, 0),
            TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ResolveWeekly_AfterLatenessWindow_ReturnsNone()
    {
        var now = new DateTimeOffset(2026, 10, 5, 18, 0, 0, TimeSpan.Zero);

        Assert.Null(LocalSchedule.ResolveWeekly(
            Rome,
            now,
            DayOfWeek.Sunday,
            new TimeOnly(20, 0),
            TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ResolveWeekly_AtDueInstant_ReturnsOccurrence()
    {
        var now = new DateTimeOffset(2026, 10, 4, 18, 0, 0, TimeSpan.Zero);

        var occurrence = LocalSchedule.ResolveWeekly(
            Rome,
            now,
            DayOfWeek.Sunday,
            new TimeOnly(20, 0),
            TimeSpan.FromHours(24));

        Assert.NotNull(occurrence);
        Assert.Equal(now, occurrence.DueAtUtc);
    }

    [Fact]
    public void ResolveWeekly_LateButInsideLateness_ReturnsSameLocalDate()
    {
        // Monday 19:59:59 local: still Sunday's occurrence, keyed by the local Sunday date.
        var now = new DateTimeOffset(2026, 10, 5, 17, 59, 59, TimeSpan.Zero);

        var occurrence = LocalSchedule.ResolveWeekly(
            Rome,
            now,
            DayOfWeek.Sunday,
            new TimeOnly(20, 0),
            TimeSpan.FromHours(24));

        Assert.NotNull(occurrence);
        Assert.Equal(new DateOnly(2026, 10, 4), occurrence.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 18, 0, 0, TimeSpan.Zero), occurrence.DueAtUtc);
    }

    [Fact]
    public void ResolveWeekly_AfterExpiry_ReturnsNone()
    {
        var now = new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);

        Assert.Null(LocalSchedule.ResolveWeekly(
            Rome,
            now,
            DayOfWeek.Sunday,
            new TimeOnly(20, 0),
            TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ResolveWeekly_SpringForwardGap_ShiftsForwardByGapLength()
    {
        // AUTO-001 §6: 2026-03-29 02:30 does not exist in Europe/Rome → 03:30 CEST = 01:30Z.
        var now = new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero);

        var occurrence = LocalSchedule.ResolveWeekly(
            Rome,
            now,
            DayOfWeek.Sunday,
            new TimeOnly(2, 30),
            TimeSpan.FromHours(24));

        Assert.NotNull(occurrence);
        Assert.Equal(new DateOnly(2026, 3, 29), occurrence.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), occurrence.DueAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 3, 30, 1, 30, 0, TimeSpan.Zero), occurrence.ExpiresAtUtc);
        Assert.Equal(new TimeOnly(3, 30), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(occurrence.DueAtUtc, Rome).DateTime));
    }

    [Fact]
    public void ResolveWeekly_SpringForwardGap_IsNotDueBeforeShiftedInstant()
    {
        // 03:00 CEST (01:00Z) is the first valid local minute, but the documented due time is 03:30 CEST.
        var now = new DateTimeOffset(2026, 3, 29, 1, 29, 59, TimeSpan.Zero);

        Assert.Null(LocalSchedule.ResolveWeekly(
            Rome,
            now,
            DayOfWeek.Sunday,
            new TimeOnly(2, 30),
            TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ResolveWeekly_FallBackOverlap_UsesEarlierInstant()
    {
        var now = new DateTimeOffset(2026, 10, 25, 0, 31, 0, TimeSpan.Zero);

        var occurrence = LocalSchedule.ResolveWeekly(
            Rome,
            now,
            DayOfWeek.Sunday,
            new TimeOnly(2, 30),
            TimeSpan.FromHours(24));

        Assert.NotNull(occurrence);
        Assert.Equal(new DateOnly(2026, 10, 25), occurrence.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), occurrence.DueAtUtc);
    }

    [Fact]
    public void ResolveWeekly_RejectsNonPositiveLateness()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LocalSchedule.ResolveWeekly(
                Rome,
                DateTimeOffset.UtcNow,
                DayOfWeek.Sunday,
                new TimeOnly(20, 0),
                TimeSpan.Zero));
    }

    [Fact]
    public void ResolveWeekly_HalfHourSpringGap_ShiftsByThirtyMinutes()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Lord_Howe");
        // 02:15 becomes 02:45 (+11), rather than the first valid minute 02:30.
        var due = new DateTimeOffset(2026, 10, 3, 15, 45, 0, TimeSpan.Zero);
        var occurrence = LocalSchedule.ResolveWeekly(zone, due, DayOfWeek.Sunday, new TimeOnly(2, 15), TimeSpan.FromHours(24));
        Assert.NotNull(occurrence);
        Assert.Equal(due, occurrence.DueAtUtc);
        Assert.Equal(new DateOnly(2026, 10, 4), occurrence.LocalDate);
    }

    [Fact]
    public void ResolveWeekly_TwoHourGapCrossingMidnight_UsesPreviousDaysOffset()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 23, 0, 0), 3, 28);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 25);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(2), start, end);
        var zone = TimeZoneInfo.CreateCustomTimeZone("MidnightGap", TimeSpan.Zero, "MidnightGap", "Standard", "Daylight", [rule]);
        var due = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero);
        Assert.True(zone.IsInvalidTime(new DateTime(2026, 3, 29, 0, 30, 0, DateTimeKind.Unspecified)));
        var occurrence = LocalSchedule.ResolveWeekly(zone, due, DayOfWeek.Sunday, new TimeOnly(0, 30), TimeSpan.FromHours(24));
        Assert.NotNull(occurrence);
        Assert.Equal(due, occurrence.DueAtUtc);
    }

    [Fact]
    public void ResolveWeekly_SkippedCalendarDate_ShiftsByTwentyFourHours()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Apia");
        // Friday 2011-12-30 was skipped: Friday noon (-10) shifts to Saturday noon (+14).
        var due = new DateTimeOffset(2011, 12, 30, 22, 0, 0, TimeSpan.Zero);
        var occurrence = LocalSchedule.ResolveWeekly(zone, due, DayOfWeek.Friday, new TimeOnly(12, 0), TimeSpan.FromHours(24));
        Assert.NotNull(occurrence);
        Assert.Equal(due, occurrence.DueAtUtc);
        Assert.Equal(new DateOnly(2011, 12, 30), occurrence.LocalDate);
    }

    // ---- ResolveDaily (AUTO-003A) ----

    private static readonly TimeOnly NineAm = new(9, 0);

    [Fact]
    public void ResolveDaily_NormalDay_IsTodayFromTheLocalTime()
    {
        // 2026-10-06 09:00 CEST = 07:00Z.
        var occurrence = LocalSchedule.ResolveDaily(Rome, new DateTimeOffset(2026, 10, 6, 7, 3, 0, TimeSpan.Zero), NineAm, TimeSpan.FromHours(24));

        Assert.NotNull(occurrence);
        Assert.Equal(new DateOnly(2026, 10, 6), occurrence.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 7, 0, 0, TimeSpan.Zero), occurrence.DueAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 7, 0, 0, TimeSpan.Zero), occurrence.ExpiresAtUtc);
    }

    [Fact]
    public void ResolveDaily_AtDueInstant_IsDue()
    {
        var now = new DateTimeOffset(2026, 10, 6, 7, 0, 0, TimeSpan.Zero);

        Assert.Equal(now, LocalSchedule.ResolveDaily(Rome, now, NineAm, TimeSpan.FromHours(24))!.DueAtUtc);
    }

    [Fact]
    public void ResolveDaily_BeforeTodaysTime_IsYesterdaysOccurrence_WhileItsWindowIsOpen()
    {
        // 08:59 local: today's 09:00 is not reached; yesterday's (due 2026-10-05 07:00Z) is still open.
        var now = new DateTimeOffset(2026, 10, 6, 6, 59, 0, TimeSpan.Zero);

        var occurrence = LocalSchedule.ResolveDaily(Rome, now, NineAm, TimeSpan.FromHours(24));

        Assert.NotNull(occurrence);
        Assert.Equal(new DateOnly(2026, 10, 5), occurrence.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 7, 0, 0, TimeSpan.Zero), occurrence.DueAtUtc);
    }

    [Fact]
    public void ResolveDaily_AfterExpiry_ReturnsNone()
    {
        // Lateness 2 h: today's window is [07:00Z, 09:00Z); yesterday's ended long ago.
        Assert.Null(LocalSchedule.ResolveDaily(Rome, new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero), NineAm, TimeSpan.FromHours(2)));
        Assert.Null(LocalSchedule.ResolveDaily(Rome, new DateTimeOffset(2026, 10, 6, 6, 59, 0, TimeSpan.Zero), NineAm, TimeSpan.FromHours(2)));
        Assert.NotNull(LocalSchedule.ResolveDaily(Rome, new DateTimeOffset(2026, 10, 6, 8, 59, 59, TimeSpan.Zero), NineAm, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void ResolveDaily_OccurrenceKeyIsTheLocalDate_NotTheUtcDate()
    {
        // Pacific/Auckland is UTC+13 (NZDT) in October: 2026-10-07 09:00 local = 2026-10-06 20:00Z.
        var auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");

        var occurrence = LocalSchedule.ResolveDaily(auckland, new DateTimeOffset(2026, 10, 6, 20, 5, 0, TimeSpan.Zero), NineAm, TimeSpan.FromHours(24));

        Assert.NotNull(occurrence);
        Assert.Equal(new DateOnly(2026, 10, 7), occurrence.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 20, 0, 0, TimeSpan.Zero), occurrence.DueAtUtc);
    }

    [Fact]
    public void ResolveDaily_SpringForwardGap_ShiftsForwardByGapLength()
    {
        // 2026-03-29 02:30 does not exist in Europe/Rome → 03:30 CEST = 01:30Z (same rule as ResolveWeekly).
        var occurrence = LocalSchedule.ResolveDaily(Rome, new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), new TimeOnly(2, 30), TimeSpan.FromHours(24));

        Assert.NotNull(occurrence);
        Assert.Equal(new DateOnly(2026, 3, 29), occurrence.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), occurrence.DueAtUtc);
        Assert.Null(LocalSchedule.ResolveDaily(Rome, new DateTimeOffset(2026, 3, 29, 1, 29, 0, TimeSpan.Zero), new TimeOnly(2, 30), TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ResolveDaily_SpringForwardDay_HasA23HourGapBetweenDueTimes_AndPrefersTheMostRecent()
    {
        // 09:00 CET on 03-28 = 08:00Z; 09:00 CEST on 03-29 = 07:00Z. At 07:30Z both windows are open:
        // the most recent local date wins.
        var occurrence = LocalSchedule.ResolveDaily(Rome, new DateTimeOffset(2026, 3, 29, 7, 30, 0, TimeSpan.Zero), NineAm, TimeSpan.FromHours(24));

        Assert.Equal((new DateOnly(2026, 3, 29), new DateTimeOffset(2026, 3, 29, 7, 0, 0, TimeSpan.Zero)),
            (occurrence!.LocalDate, occurrence.DueAtUtc));

        var before = LocalSchedule.ResolveDaily(Rome, new DateTimeOffset(2026, 3, 29, 6, 59, 0, TimeSpan.Zero), NineAm, TimeSpan.FromHours(24));
        Assert.Equal((new DateOnly(2026, 3, 28), new DateTimeOffset(2026, 3, 28, 8, 0, 0, TimeSpan.Zero)), (before!.LocalDate, before.DueAtUtc));
    }

    [Fact]
    public void ResolveDaily_FallBackAmbiguousTime_UsesTheFirstOccurrence_AndOneLocalDate()
    {
        // 2026-10-25 02:30 occurs twice in Europe/Rome: first 02:30 CEST = 00:30Z, then 02:30 CET = 01:30Z.
        var first = LocalSchedule.ResolveDaily(Rome, new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), new TimeOnly(2, 30), TimeSpan.FromHours(24));
        var second = LocalSchedule.ResolveDaily(Rome, new DateTimeOffset(2026, 10, 25, 1, 45, 0, TimeSpan.Zero), new TimeOnly(2, 30), TimeSpan.FromHours(24));

        Assert.Equal((new DateOnly(2026, 10, 25), new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero)), (first!.LocalDate, first.DueAtUtc));
        Assert.Equal((first.LocalDate, first.DueAtUtc), (second!.LocalDate, second.DueAtUtc));
    }

    [Fact]
    public void ResolveDaily_FallBackDay_HasA25HourGapBetweenDueTimes()
    {
        // 09:00 CEST on 10-24 = 07:00Z (expires 10-25 07:00Z); 09:00 CET on 10-25 = 08:00Z. Between them
        // no daily occurrence is open with a 24 h lateness.
        Assert.Null(LocalSchedule.ResolveDaily(Rome, new DateTimeOffset(2026, 10, 25, 7, 30, 0, TimeSpan.Zero), NineAm, TimeSpan.FromHours(24)));

        var today = LocalSchedule.ResolveDaily(Rome, new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero), NineAm, TimeSpan.FromHours(24));
        Assert.Equal(new DateOnly(2026, 10, 25), today!.LocalDate);
    }

    [Fact]
    public void ResolveDaily_RejectsANonPositiveLateness()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalSchedule.ResolveDaily(Rome, DateTimeOffset.UnixEpoch, NineAm, TimeSpan.Zero));
    }
}
