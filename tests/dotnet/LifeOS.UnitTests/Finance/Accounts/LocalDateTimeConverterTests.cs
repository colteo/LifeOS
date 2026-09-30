using LifeOS.App.Services.Finance;

namespace LifeOS.UnitTests.Finance.Accounts;

public class LocalDateTimeConverterTests
{
    // A fixed Central European zone (UTC+1, UTC+2 in summer), independent of the machine running the tests.
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
    public void SummerTime_ConvertsWithTheSummerOffset()
    {
        Assert.True(LocalDateTimeConverter.TryToUtc(new DateTime(2026, 9, 30, 18, 0, 0), Rome, out var utc, out _));

        Assert.Equal(new DateTimeOffset(2026, 9, 30, 16, 0, 0, TimeSpan.Zero), utc);
        Assert.Equal(TimeSpan.Zero, utc.Offset);
    }

    [Fact]
    public void WinterTime_ConvertsWithTheStandardOffset()
    {
        Assert.True(LocalDateTimeConverter.TryToUtc(new DateTime(2026, 1, 15, 18, 0, 0), Rome, out var utc, out _));

        Assert.Equal(new DateTimeOffset(2026, 1, 15, 17, 0, 0, TimeSpan.Zero), utc);
    }

    [Fact]
    public void SkippedTime_IsRejected()
    {
        // 2026-03-29: clocks jump from 02:00 to 03:00.
        Assert.False(LocalDateTimeConverter.TryToUtc(new DateTime(2026, 3, 29, 2, 30, 0), Rome, out _, out var error));

        Assert.NotNull(error);
    }

    [Fact]
    public void RepeatedTime_IsRejected()
    {
        // 2026-10-25: 02:00-03:00 happens twice.
        Assert.False(LocalDateTimeConverter.TryToUtc(new DateTime(2026, 10, 25, 2, 30, 0), Rome, out _, out var error));

        Assert.NotNull(error);
    }
}
