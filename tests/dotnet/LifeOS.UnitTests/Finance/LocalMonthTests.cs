using LifeOS.App.Services.Finance;

namespace LifeOS.UnitTests.Finance;

public class LocalMonthTests
{
    // Central European time: UTC+1, UTC+2 from the last Sunday of March 02:00 to the last Sunday of
    // October 03:00.
    private static readonly TimeZoneInfo Rome = TimeZoneInfo.CreateCustomTimeZone(
        "Test/Rome",
        TimeSpan.FromHours(1),
        "Test Rome",
        "CET",
        "CEST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                DateTime.MinValue.Date,
                DateTime.MaxValue.Date,
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday))
        ]);

    [Fact]
    public void Containing_IsTheFirstDayOfTheMonthAtMidnight()
    {
        Assert.Equal(new DateTime(2026, 9, 1), LocalMonth.Containing(new DateTime(2026, 9, 27, 18, 5, 0)));
    }

    [Fact]
    public void September_IsTheLocalMonthInUtc()
    {
        var (from, to) = LocalMonth.UtcRange(new DateTime(2026, 9, 15), Rome);

        Assert.Equal(new DateTimeOffset(2026, 8, 31, 22, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.Zero), to);
        Assert.Equal(TimeSpan.Zero, from.Offset);
    }

    [Fact]
    public void AMonthWithADaylightSavingChange_HasTheRightLength()
    {
        // October 2026: starts at UTC+2, ends at UTC+1 (one hour longer than 31 days).
        var (from, to) = LocalMonth.UtcRange(new DateTime(2026, 10, 1), Rome);

        Assert.Equal(new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.Zero), from);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero), to);
        Assert.True(to - from < TimeSpan.FromDays(32));
    }

    [Fact]
    public void AMidnightThatDoesNotExist_StartsAtTheFirstValidMinute()
    {
        // A zone (UTC-3) that springs forward at 00:00 on 1 April: 00:00-00:59 do not exist.
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test/MidnightGap",
            TimeSpan.FromHours(-3),
            "Midnight gap",
            "STD",
            "DST",
            [
                TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    DateTime.MinValue.Date,
                    DateTime.MaxValue.Date,
                    TimeSpan.FromHours(1),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 4, 1),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 10, 1))
            ]);

        var (from, _) = LocalMonth.UtcRange(new DateTime(2026, 4, 1), zone);

        // Local 01:00 at UTC-2.
        Assert.Equal(new DateTimeOffset(2026, 4, 1, 3, 0, 0, TimeSpan.Zero), from);
    }

    [Fact]
    public void Label_UsesEnglishMonthNames()
    {
        Assert.Equal("September 2026", LocalMonth.Label(new DateTime(2026, 9, 27)));
    }
}
