using LifeOS.Domain.Nutrition.Hydration;

namespace LifeOS.UnitTests.Nutrition.Hydration;

// HYD-001 §5 invariants 4-6 and 8: target bounds, the open-ended target schedule, quick-add amounts.
public class HydrationTargetTests
{
    private static readonly Guid User = new("01a0ea76-0c00-7000-8000-00000000000a");
    private static readonly DateOnly Day = new(2026, 10, 14);
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    // ---- Target versions ----

    [Theory]
    [InlineData(250)]
    [InlineData(2000)]
    [InlineData(10000)]
    [InlineData(null)]
    public void Target_AcceptsNullOr250To10000(int? target) =>
        Assert.Equal(target, HydrationTargetVersion.Create(User, Day, target, Now).TargetMl);

    [Theory]
    [InlineData(249)]
    [InlineData(10001)]
    [InlineData(0)]
    [InlineData(-2000)]
    public void Target_RejectsValuesOutside250To10000(int target) =>
        Assert.Equal("targetMl", Assert.Throws<ArgumentOutOfRangeException>(() => HydrationTargetVersion.Create(User, Day, target, Now)).ParamName);

    [Fact]
    public void Target_RejectsAnEmptyUserAndInfiniteDates()
    {
        Assert.Throws<ArgumentException>(() => HydrationTargetVersion.Create(Guid.Empty, Day, 2000, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => HydrationTargetVersion.Create(User, DateOnly.MinValue, 2000, Now));
    }

    // ---- Schedule ----

    [Fact]
    public void Schedule_BeforeTheFirstVersion_HasNoTarget()
    {
        var schedule = Schedule((Day, 2000));

        Assert.Null(schedule.TargetOn(Day.AddDays(-1)));
        Assert.Null(schedule.VersionOn(Day.AddDays(-1)));
        Assert.Null(new HydrationTargetSchedule([]).TargetOn(Day));
    }

    [Fact]
    public void Schedule_UsesTheLatestVersionOnOrBeforeTheDate()
    {
        var schedule = Schedule((Day.AddDays(10), 2500), (Day, 2000), (Day.AddDays(20), null));

        Assert.Equal(2000, schedule.TargetOn(Day));
        Assert.Equal(2000, schedule.TargetOn(Day.AddDays(9)));
        Assert.Equal(2500, schedule.TargetOn(Day.AddDays(10)));
        Assert.Equal(2500, schedule.TargetOn(Day.AddDays(19)));
        Assert.Equal(Day.AddDays(10), schedule.VersionOn(Day.AddDays(15))!.EffectiveFrom);
    }

    [Fact]
    public void Schedule_AfterRemoval_HasNoTarget_ButEarlierDaysKeepTheirs()
    {
        var schedule = Schedule((Day.AddDays(-5), 2000), (Day, null));

        Assert.Equal(2000, schedule.TargetOn(Day.AddDays(-1)));
        Assert.Null(schedule.TargetOn(Day));
        Assert.Null(schedule.TargetOn(Day.AddDays(30)));
        Assert.Equal(Day, schedule.VersionOn(Day)!.EffectiveFrom);
    }

    [Fact]
    public void Schedule_OnTheSameDate_TheLastVersionReplacesTheEarlier() =>
        Assert.Equal(3000, Schedule((Day, 2000), (Day, 3000)).TargetOn(Day));

    [Fact]
    public void Schedule_BuildsDaysWithTheirOwnTarget()
    {
        var schedule = Schedule((Day.AddDays(-1), 2000), (Day, 2500));

        // Yesterday's 2000 ml met its 2000 target; the same total today is under the new 2500.
        Assert.True(schedule.Day(Day.AddDays(-1), 2000).IsTargetMet);
        Assert.Equal(new HydrationDay(Day, 2000, 2500), schedule.Day(Day, 2000));
        Assert.False(schedule.Day(Day, 2000).IsTargetMet);
    }

    // ---- Quick-add settings ----

    [Fact]
    public void Settings_DefaultTo150_250_500()
    {
        var settings = HydrationSettings.Default(User);

        Assert.Equal([150, 250, 500], settings.QuickAddMl);
        Assert.Equal(DateTimeOffset.MinValue, settings.UpdatedAtUtc);
    }

    [Fact]
    public void Settings_AcceptThreeAmounts_DuplicatesIncluded()
    {
        Assert.Equal([200, 330, 750], HydrationSettings.Create(User, [200, 330, 750], Now).QuickAddMl);
        Assert.Equal([250, 250, 250], HydrationSettings.Create(User, [250, 250, 250], Now).QuickAddMl);
    }

    [Theory]
    [InlineData(new[] { 250, 500 }, "amountsMl")]
    [InlineData(new[] { 150, 250, 500, 750 }, "amountsMl")]
    [InlineData(new[] { 150, 0, 500 }, "amountsMl[1]")]
    [InlineData(new[] { 150, 250, 4001 }, "amountsMl[2]")]
    public void Settings_RejectAnythingButThreeValidAmounts(int[] amounts, string field) =>
        Assert.Equal(field, Assert.ThrowsAny<ArgumentException>(() => HydrationSettings.Create(User, amounts, Now)).ParamName);

    private static HydrationTargetSchedule Schedule(params (DateOnly From, int? Target)[] versions) =>
        new(versions.Select(version => HydrationTargetVersion.Create(User, version.From, version.Target, Now)));
}
