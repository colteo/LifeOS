using LifeOS.Domain.Notifications;

namespace LifeOS.UnitTests.Notifications;

// AUTO-003A: quiet hours are local wall-clock ranges [Start, End), possibly across midnight, and the
// next allowed instant is computed in the user's IANA zone (never as a fixed UTC time), DST included.
public class QuietHoursTests
{
    private static TimeZoneInfo Rome => TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    // ---- Model ----

    [Fact]
    public void Default_Is22To08_AcrossMidnight()
    {
        Assert.Equal((new TimeOnly(22, 0), new TimeOnly(8, 0), true),
            (QuietHours.Default.Start, QuietHours.Default.End, QuietHours.Default.CrossesMidnight));
    }

    [Fact]
    public void Create_RejectsEqualTimes_AndSeconds()
    {
        Assert.Throws<ArgumentException>(() => QuietHours.Create(new TimeOnly(22, 0), new TimeOnly(22, 0)));
        Assert.Throws<ArgumentException>(() => QuietHours.Create(new TimeOnly(22, 0, 30), new TimeOnly(8, 0)));
        Assert.False(QuietHours.IsValid(new TimeOnly(7, 0), new TimeOnly(7, 0)));
        Assert.True(QuietHours.IsValid(new TimeOnly(0, 0), new TimeOnly(23, 59)));
    }

    [Theory]
    [InlineData("12:59", false)]
    [InlineData("13:00", true)]
    [InlineData("14:59", true)]
    [InlineData("15:00", false)]
    public void SameDayRange_StartIsInclusive_EndIsExclusive(string time, bool quiet)
    {
        Assert.Equal(quiet, QuietHours.Create(new TimeOnly(13, 0), new TimeOnly(15, 0)).Contains(TimeOnly.Parse(time)));
    }

    [Theory]
    [InlineData("21:59", false)]
    [InlineData("22:00", true)]
    [InlineData("23:59", true)]
    [InlineData("00:00", true)]
    [InlineData("07:59", true)]
    [InlineData("08:00", false)]
    [InlineData("12:00", false)]
    public void CrossMidnightRange_WrapsAtMidnight(string time, bool quiet)
    {
        Assert.Equal(quiet, QuietHours.Default.Contains(TimeOnly.Parse(time)));
    }

    // ---- Next allowed instant ----

    [Fact]
    public void NextAllowed_OutsideQuietHours_IsTheInstantItself()
    {
        var noon = Utc(10, 6, 10); // 12:00 CEST

        Assert.Equal(noon, QuietHours.Default.NextAllowedUtc(Rome, noon));
    }

    [Fact]
    public void NextAllowed_ExactlyAtTheEnd_IsAllowed_ExactlyAtTheStart_IsNot()
    {
        Assert.Equal(Utc(10, 6, 6), QuietHours.Default.NextAllowedUtc(Rome, Utc(10, 6, 6)));    // 08:00 CEST
        Assert.Equal(Utc(10, 7, 6), QuietHours.Default.NextAllowedUtc(Rome, Utc(10, 6, 20)));   // 22:00 CEST → 08:00 next day
    }

    [Fact]
    public void NextAllowed_BeforeMidnight_IsTomorrowsEnd_AfterMidnight_IsTodaysEnd()
    {
        Assert.Equal(Utc(10, 7, 6), QuietHours.Default.NextAllowedUtc(Rome, Utc(10, 6, 21)));   // 23:00 CEST
        Assert.Equal(Utc(10, 7, 6), QuietHours.Default.NextAllowedUtc(Rome, Utc(10, 7, 3)));    // 05:00 CEST
    }

    [Fact]
    public void NextAllowed_SameDayRange_IsTheEndOfThatDay()
    {
        var afternoon = QuietHours.Create(new TimeOnly(13, 0), new TimeOnly(15, 0));

        Assert.Equal(Utc(10, 6, 13), afternoon.NextAllowedUtc(Rome, Utc(10, 6, 12)));           // 14:00 → 15:00 CEST
    }

    [Fact]
    public void NextAllowed_UsesTheZone_NotAFixedUtcTime()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // 23:00 EDT on 2026-10-06 = 03:00Z on 10-07 → 08:00 EDT = 12:00Z.
        Assert.Equal(Utc(10, 7, 12), QuietHours.Default.NextAllowedUtc(newYork, Utc(10, 7, 3)));
    }

    [Fact]
    public void NextAllowed_SpringForward_EndsAtTheLocalEnd_WithTheNewOffset()
    {
        // Night 03-28 → 03-29: 23:00 CET (22:00Z) → 08:00 CEST = 06:00Z, not 07:00Z.
        Assert.Equal(Utc(3, 29, 6), QuietHours.Default.NextAllowedUtc(Rome, Utc(3, 28, 22)));
    }

    [Fact]
    public void NextAllowed_SpringForward_EndInsideTheGap_IsWhenTheClockJumpsPastIt()
    {
        // 02:30 does not exist on 03-29: the wall clock jumps from 02:00 CET to 03:00 CEST at 01:00Z.
        var quietHours = QuietHours.Create(new TimeOnly(22, 0), new TimeOnly(2, 30));

        Assert.Equal(Utc(3, 29, 1), quietHours.NextAllowedUtc(Rome, Utc(3, 28, 22)));
        Assert.False(quietHours.Contains(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(Utc(3, 29, 1), Rome).DateTime)));
    }

    [Fact]
    public void NextAllowed_FallBack_EndsAtTheLocalEnd_WithTheNewOffset()
    {
        // Night 10-24 → 10-25: 23:00 CEST (21:00Z) → 08:00 CET = 07:00Z.
        Assert.Equal(Utc(10, 25, 7), QuietHours.Default.NextAllowedUtc(Rome, Utc(10, 24, 21)));
    }

    [Fact]
    public void NextAllowed_FallBack_AmbiguousEnd_IsItsNextOccurrence()
    {
        // 02:30 occurs at 00:30Z (CEST) and again at 01:30Z (CET). Quiet 22:00–02:30: before the first
        // 02:30 the end is the first; when the clock falls back into 02:00–02:30 it is quiet again until
        // the second.
        var quietHours = QuietHours.Create(new TimeOnly(22, 0), new TimeOnly(2, 30));

        Assert.Equal(Utc(10, 25, 0, 30), quietHours.NextAllowedUtc(Rome, Utc(10, 24, 21)));
        Assert.Equal(Utc(10, 25, 0, 45), quietHours.NextAllowedUtc(Rome, Utc(10, 25, 0, 45)));  // 02:45 CEST: allowed
        Assert.Equal(Utc(10, 25, 1, 30), quietHours.NextAllowedUtc(Rome, Utc(10, 25, 1, 10)));  // 02:10 CET: quiet again
    }
}
