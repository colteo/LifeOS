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
    public void ResolveWeekly_SpringForwardGap_ShiftsToFirstValidLocalTime()
    {
        var now = new DateTimeOffset(2026, 3, 29, 1, 31, 0, TimeSpan.Zero);

        var occurrence = LocalSchedule.ResolveWeekly(
            Rome,
            now,
            DayOfWeek.Sunday,
            new TimeOnly(2, 30),
            TimeSpan.FromHours(24));

        Assert.NotNull(occurrence);
        Assert.Equal(new DateOnly(2026, 3, 29), occurrence.LocalDate);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero), occurrence.DueAtUtc);
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
