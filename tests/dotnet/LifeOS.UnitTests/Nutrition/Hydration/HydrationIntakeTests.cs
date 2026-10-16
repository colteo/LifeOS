using LifeOS.Domain.Nutrition.Hydration;

namespace LifeOS.UnitTests.Nutrition.Hydration;

// HYD-001 §5 invariants 1-3 and 7: intake amounts, diary time model, and the computed day.
public class HydrationIntakeTests
{
    private static readonly Guid User = new("01a0ea76-0c00-7000-8000-00000000000a");
    private static readonly DateOnly Day = new(2026, 10, 14);
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 6, 10, 4, TimeSpan.Zero);

    [Theory]
    [InlineData(1)]
    [InlineData(250)]
    [InlineData(4000)]
    public void Create_AcceptsAmountsFrom1To4000(int amount) =>
        Assert.Equal(amount, HydrationIntake.Create(User, amount, Day, new TimeOnly(8, 10), 120, Now).AmountMl);

    [Theory]
    [InlineData(0)]
    [InlineData(-250)]
    [InlineData(4001)]
    [InlineData(25000)]
    public void Create_RejectsAmountsOutside1To4000(int amount)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => HydrationIntake.Create(User, amount, Day, new TimeOnly(8, 10), 120, Now));

        Assert.Equal("amountMl", exception.ParamName);
    }

    [Fact]
    public void Create_DerivesTheUtcInstantFromTheLocalDiaryTime()
    {
        var intake = HydrationIntake.Create(User, 250, Day, new TimeOnly(8, 10), 120, Now);

        Assert.Equal(new DateTimeOffset(2026, 10, 14, 6, 10, 0, TimeSpan.Zero), intake.OccurredAtUtc);
        Assert.Equal((Day, new TimeOnly(8, 10), 120), (intake.DiaryDate, intake.DiaryTime, intake.UtcOffsetMinutes));
        Assert.Equal((Now, Now), (intake.CreatedAtUtc, intake.UpdatedAtUtc));
        Assert.Equal(7, intake.Id.Version);
    }

    [Fact]
    public void Create_RequiresWholeMinutes() =>
        Assert.Equal("time", Assert.Throws<ArgumentException>(() =>
            HydrationIntake.Create(User, 250, Day, new TimeOnly(8, 10, 30), 120, Now)).ParamName);

    [Theory]
    [InlineData(-840, true)]
    [InlineData(840, true)]
    [InlineData(-841, false)]
    [InlineData(841, false)]
    public void Create_AcceptsOffsetsWithin14Hours(int offset, bool valid)
    {
        var create = () => HydrationIntake.Create(User, 250, Day, new TimeOnly(8, 10), offset, Now);

        if (valid)
        {
            Assert.Equal(offset, create().UtcOffsetMinutes);
        }
        else
        {
            Assert.Equal("utcOffsetMinutes", Assert.Throws<ArgumentOutOfRangeException>(create).ParamName);
        }
    }

    [Fact]
    public void Create_RejectsAnEmptyUserAndInfiniteDates()
    {
        Assert.Throws<ArgumentException>(() => HydrationIntake.Create(Guid.Empty, 250, Day, new TimeOnly(8, 10), 0, Now));
        Assert.Equal("date", Assert.Throws<ArgumentOutOfRangeException>(() =>
            HydrationIntake.Create(User, 250, DateOnly.MaxValue, new TimeOnly(8, 10), 0, Now)).ParamName);
    }

    [Fact]
    public void Update_KeepsTheDiaryDayAndTheOriginalOffset()
    {
        var intake = HydrationIntake.Create(User, 250, Day, new TimeOnly(8, 10), 120, Now);
        var later = Now.AddHours(3);

        intake.Update(500, new TimeOnly(23, 30), later);

        Assert.Equal((500, Day, new TimeOnly(23, 30), 120), (intake.AmountMl, intake.DiaryDate, intake.DiaryTime, intake.UtcOffsetMinutes));
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 21, 30, 0, TimeSpan.Zero), intake.OccurredAtUtc);
        Assert.Equal((Now, later), (intake.CreatedAtUtc, intake.UpdatedAtUtc));
    }

    [Fact]
    public void Update_ValidatesLikeCreate_AndChangesNothingWhenInvalid()
    {
        var intake = HydrationIntake.Create(User, 250, Day, new TimeOnly(8, 10), 120, Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => intake.Update(0, new TimeOnly(9, 0), Now));

        Assert.Equal((250, new TimeOnly(8, 10)), (intake.AmountMl, intake.DiaryTime));
    }

    // ---- HydrationDay ----

    [Theory]
    [InlineData(1250, 2000, false, 62)]
    [InlineData(2000, 2000, true, 100)]
    [InlineData(2750, 2000, true, 137)]
    [InlineData(0, 2000, false, 0)]
    [InlineData(10000, 2000, true, 500)]
    public void Day_IsMetAtOrOverTheTarget_WithAFlooredPercent(int total, int target, bool met, int percent)
    {
        var day = new HydrationDay(Day, total, target);

        Assert.Equal((met, (int?)percent), (day.IsTargetMet, day.ProgressPercent));
    }

    [Fact]
    public void Day_WithoutATarget_IsNeverMet_AndHasNoPercent()
    {
        var day = new HydrationDay(Day, 5000, null);

        Assert.False(day.IsTargetMet);
        Assert.Null(day.ProgressPercent);
    }

    [Fact]
    public void Day_RejectsANegativeTotal() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HydrationDay(Day, -1, null));
}
