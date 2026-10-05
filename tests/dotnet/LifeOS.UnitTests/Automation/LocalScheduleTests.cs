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
}
