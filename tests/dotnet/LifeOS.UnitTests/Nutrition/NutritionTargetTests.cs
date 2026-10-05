using LifeOS.Domain.Nutrition;

namespace LifeOS.UnitTests.Nutrition;

// NUT-003 domain: target values, bounded plans with a weekly pattern, daily overrides and the
// deterministic resolution order.
public class NutritionTargetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid User = Guid.CreateVersion7();

    // 7 Oct 2026 is a Wednesday.
    private static readonly DateOnly Oct7 = new(2026, 10, 7);
    private static readonly DateOnly Nov3 = new(2026, 11, 3);

    private static readonly NutritionTargetValues Base = NutritionTargetValues.Create(2200, 160, 240, 70);
    private static readonly NutritionTargetValues Training = NutritionTargetValues.Create(2500, null, null, null);
    private static readonly NutritionTargetValues Sunday = NutritionTargetValues.Create(2800, null, null, null);

    // ---- Values ----

    [Theory]
    [InlineData(2200, null, null, null)]
    [InlineData(null, 160, null, null)]
    [InlineData(2200, 160, 240, 70)]
    public void PartialTargets_LeaveUntargetedMetricsNull(int? kcal, int? protein, int? carbs, int? fat)
    {
        var values = NutritionTargetValues.Create(kcal, protein, carbs, fat);

        Assert.Equal(((decimal?)kcal, (decimal?)protein, (decimal?)carbs, (decimal?)fat),
            (values.CaloriesKcal, values.ProteinGrams, values.CarbsGrams, values.FatGrams));
    }

    [Fact]
    public void AnEmptyTarget_IsRejected()
    {
        Assert.StartsWith(NutritionTargetValues.NoneMessage,
            Assert.Throws<ArgumentException>(() => NutritionTargetValues.Create(null, null, null, null)).Message);
    }

    [Theory]
    [InlineData("0", null, "caloriesKcal")]
    [InlineData("-1", null, "caloriesKcal")]
    [InlineData("0.04", null, "caloriesKcal")]
    [InlineData("10000.1", null, "caloriesKcal")]
    [InlineData(null, "0", "proteinGrams")]
    [InlineData(null, "1000.06", "proteinGrams")]
    public void ZeroNegativeAndTooHigh_AreRejected_NamingTheField(string? kcal, string? protein, string field)
    {
        var exception = Assert.ThrowsAny<ArgumentOutOfRangeException>(() => NutritionTargetValues.Create(Parse(kcal), Parse(protein), null, null));

        Assert.Equal(field, exception.ParamName);
    }

    [Fact]
    public void BoundsAreInclusive_AndValuesKeepOneDecimal()
    {
        var max = NutritionTargetValues.Create(10000, 1000, 1000, 1000);
        var rounded = NutritionTargetValues.Create(2199.95m, 160.04m, 0.05m, null);

        Assert.Equal((10000m, 1000m, 1000m, 1000m), (max.CaloriesKcal, max.ProteinGrams, max.CarbsGrams, max.FatGrams));
        Assert.Equal((2200.0m, 160.0m, 0.1m), (rounded.CaloriesKcal, rounded.ProteinGrams, rounded.CarbsGrams));
    }

    // ---- Plans ----

    [Fact]
    public void AValidBoundedPlan_HasSevenRules_MondayToSunday()
    {
        var plan = Plan(Oct7, Nov3, Base, AllDefault());

        Assert.Equal((Oct7, Nov3), (plan.StartsOn, plan.EndsOn));
        Assert.Equal(Base, plan.DefaultTarget);
        Assert.Equal(NutritionTargetPlan.Week, plan.Rules.Select(rule => rule.Weekday));
        Assert.All(plan.Rules, rule => Assert.Equal(NutritionTargetDayMode.Default, rule.Mode));
        Assert.Equal(7, plan.Id.Version);
        Assert.Equal((Now, Now), (plan.CreatedAtUtc, plan.UpdatedAtUtc));
    }

    [Fact]
    public void EndBeforeStart_IsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => Plan(Nov3, Oct7, Base, AllDefault()));

        Assert.Equal("endsOn", exception.ParamName);
    }

    [Fact]
    public void AOneDayPeriod_IsValid()
    {
        var plan = Plan(Oct7, Oct7, Base, AllDefault());

        Assert.True(plan.Covers(Oct7));
        Assert.False(plan.Covers(Oct7.AddDays(1)));
    }

    [Fact]
    public void APartialDefault_IsValid()
    {
        var plan = Plan(Oct7, Nov3, NutritionTargetValues.Create(null, 160, null, null), AllDefault());

        Assert.Equal(160m, plan.WeekdayTarget(Oct7)!.ProteinGrams);
        Assert.Null(plan.WeekdayTarget(Oct7)!.CaloriesKcal);
    }

    [Fact]
    public void NoDefault_IsValidWhenNoWeekdayUsesIt()
    {
        var rules = NutritionTargetPlan.Week
            .Select(day => day == DayOfWeek.Monday
                ? new NutritionTargetDayRuleSpec(day, NutritionTargetDayMode.Custom, Training)
                : new NutritionTargetDayRuleSpec(day, NutritionTargetDayMode.NoTarget))
            .ToList();

        var plan = Plan(Oct7, Nov3, null, rules);

        Assert.Null(plan.DefaultTarget);
        Assert.Equal(2500m, plan.WeekdayTarget(new DateOnly(2026, 10, 12))!.CaloriesKcal);
        Assert.Null(plan.WeekdayTarget(Oct7));
    }

    [Fact]
    public void ADefaultWeekday_WithoutAPlanDefault_IsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => Plan(Oct7, Nov3, null, AllDefault()));

        Assert.Equal("weeklyRules.Monday", exception.ParamName);
        Assert.Contains("default target", exception.Message);
    }

    [Fact]
    public void ACustomWeekday_RequiresValues()
    {
        var rules = AllDefault().Select(rule => rule.Weekday == DayOfWeek.Friday ? rule with { Mode = NutritionTargetDayMode.Custom } : rule).ToList();

        Assert.Equal("weeklyRules.Friday", Assert.Throws<ArgumentException>(() => Plan(Oct7, Nov3, Base, rules)).ParamName);
    }

    [Theory]
    [InlineData(NutritionTargetDayMode.Default)]
    [InlineData(NutritionTargetDayMode.NoTarget)]
    public void DefaultAndNoTargetWeekdays_CarryNoValues(NutritionTargetDayMode mode)
    {
        var rules = AllDefault().Select(rule => rule.Weekday == DayOfWeek.Sunday ? new NutritionTargetDayRuleSpec(DayOfWeek.Sunday, mode, Sunday) : rule).ToList();

        Assert.Equal("weeklyRules.Sunday", Assert.Throws<ArgumentException>(() => Plan(Oct7, Nov3, Base, rules)).ParamName);
    }

    [Fact]
    public void ExactlyOneRulePerWeekday_IsRequired()
    {
        var six = AllDefault().Take(6).ToList();
        var duplicate = AllDefault().Take(6).Append(new NutritionTargetDayRuleSpec(DayOfWeek.Monday, NutritionTargetDayMode.Default)).ToList();

        Assert.Equal("weeklyRules", Assert.Throws<ArgumentException>(() => Plan(Oct7, Nov3, Base, six)).ParamName);
        Assert.Equal("weeklyRules", Assert.Throws<ArgumentException>(() => Plan(Oct7, Nov3, Base, duplicate)).ParamName);
    }

    [Fact]
    public void Overlaps_UseInclusiveRanges_AndAdjacentPeriodsDoNotOverlap()
    {
        var plan = Plan(Oct7, Nov3, Base, AllDefault());

        Assert.True(plan.Overlaps(new DateOnly(2026, 10, 28), new DateOnly(2026, 11, 30)));
        Assert.True(plan.Overlaps(Nov3, Nov3));
        Assert.True(plan.Overlaps(new DateOnly(2026, 9, 1), Oct7));
        Assert.False(plan.Overlaps(Nov3.AddDays(1), new DateOnly(2026, 12, 1)));
        Assert.False(plan.Overlaps(new DateOnly(2026, 9, 1), Oct7.AddDays(-1)));
    }

    [Fact]
    public void Update_ReplacesPeriodAndPattern_KeepingIdentity()
    {
        var plan = Plan(Oct7, Nov3, Base, AllDefault());
        var id = plan.Id;

        plan.Update(Oct7, Nov3.AddDays(7), Training, TrainingWeek(), Now.AddHours(1));

        Assert.Equal(id, plan.Id);
        Assert.Equal(Nov3.AddDays(7), plan.EndsOn);
        Assert.Equal(Now, plan.CreatedAtUtc);
        Assert.Equal(Now.AddHours(1), plan.UpdatedAtUtc);
        Assert.Equal(NutritionTargetDayMode.Custom, plan.Rules.Single(rule => rule.Weekday == DayOfWeek.Monday).Mode);
    }

    [Fact]
    public void IsoWeekdays_AreMondayOneToSundaySeven()
    {
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], NutritionTargetPlan.Week.Select(NutritionTargetPlan.Iso));
        Assert.Equal(NutritionTargetPlan.Week, Enumerable.Range(1, 7).Select(NutritionTargetPlan.FromIso));
    }

    // ---- Overrides ----

    [Fact]
    public void ACustomOverride_CarriesItsTarget()
    {
        var plan = Plan(Oct7, Nov3, Base, AllDefault());
        var item = NutritionTargetOverride.Create(plan, new DateOnly(2026, 10, 23), NutritionTargetOverrideMode.Custom,
            NutritionTargetValues.Create(3000, null, null, null), Now);

        Assert.Equal((plan.Id, plan.UserId), (item.PlanId, item.UserId));
        Assert.Equal(3000m, item.Target!.CaloriesKcal);
    }

    [Fact]
    public void ANoTargetOverride_CarriesNoValues()
    {
        var plan = Plan(Oct7, Nov3, Base, AllDefault());

        Assert.Null(NutritionTargetOverride.Create(plan, Oct7, NutritionTargetOverrideMode.NoTarget, null, Now).Target);
        Assert.Throws<ArgumentException>(() => NutritionTargetOverride.Create(plan, Oct7, NutritionTargetOverrideMode.NoTarget, Base, Now));
        Assert.Throws<ArgumentException>(() => NutritionTargetOverride.Create(plan, Oct7, NutritionTargetOverrideMode.Custom, null, Now));
    }

    [Fact]
    public void AnOverrideOutsideItsPlan_IsRejected()
    {
        var plan = Plan(Oct7, Nov3, Base, AllDefault());

        Assert.Equal("date", Assert.Throws<ArgumentOutOfRangeException>(() =>
            NutritionTargetOverride.Create(plan, Nov3.AddDays(1), NutritionTargetOverrideMode.NoTarget, null, Now)).ParamName);
    }

    // ---- Resolution ----

    [Fact]
    public void NoPlan_MeansNoTarget()
    {
        Assert.Null(NutritionTargetResolution.Resolve(Oct7, null, null));
        Assert.Null(NutritionTargetResolution.Resolve(Nov3.AddDays(1), Plan(Oct7, Nov3, Base, AllDefault()), null));
    }

    [Fact]
    public void WeekdayRules_ResolveDefaultCustomAndNoTarget()
    {
        var plan = Plan(Oct7, Nov3, Base, TrainingWeek(sundayNoTarget: true));

        Assert.Equal(Training, NutritionTargetResolution.Resolve(new DateOnly(2026, 10, 12), plan, null)); // Monday
        Assert.Equal(Base, NutritionTargetResolution.Resolve(new DateOnly(2026, 10, 13), plan, null));     // Tuesday
        Assert.Equal(Training, NutritionTargetResolution.Resolve(Oct7, plan, null));                       // Wednesday
        Assert.Equal(Base, NutritionTargetResolution.Resolve(new DateOnly(2026, 10, 10), plan, null));     // Saturday
        Assert.Null(NutritionTargetResolution.Resolve(new DateOnly(2026, 10, 11), plan, null));            // Sunday
    }

    [Fact]
    public void Overrides_WinOverTheWeekdayRule_AndRemovingFallsBack()
    {
        var plan = Plan(Oct7, Nov3, Base, TrainingWeek());
        var oct21 = new DateOnly(2026, 10, 21); // Wednesday: 2500 by rule
        var custom = NutritionTargetOverride.Create(plan, oct21, NutritionTargetOverrideMode.Custom,
            NutritionTargetValues.Create(3000, null, null, null), Now);
        var none = NutritionTargetOverride.Create(plan, oct21, NutritionTargetOverrideMode.NoTarget, null, Now);

        Assert.Equal(3000m, NutritionTargetResolution.Resolve(oct21, plan, custom)!.CaloriesKcal);
        Assert.Null(NutritionTargetResolution.Resolve(oct21, plan, none));
        Assert.Equal(2500m, NutritionTargetResolution.Resolve(oct21, plan, null)!.CaloriesKcal);

        // Another date's override does not apply.
        Assert.Equal(2500m, NutritionTargetResolution.Resolve(new DateOnly(2026, 10, 28), plan, custom)!.CaloriesKcal);
    }

    [Fact]
    public void PeriodBoundaries_AreInclusive()
    {
        var plan = Plan(Oct7, Nov3, Base, AllDefault());

        Assert.Equal(Base, NutritionTargetResolution.Resolve(Oct7, plan, null));
        Assert.Equal(Base, NutritionTargetResolution.Resolve(Nov3, plan, null));
        Assert.Null(NutritionTargetResolution.Resolve(Oct7.AddDays(-1), plan, null));
        Assert.Null(NutritionTargetResolution.Resolve(Nov3.AddDays(1), plan, null));
    }

    private static NutritionTargetPlan Plan(DateOnly startsOn, DateOnly endsOn, NutritionTargetValues? defaultTarget,
        IReadOnlyCollection<NutritionTargetDayRuleSpec> rules) =>
        NutritionTargetPlan.Create(User, startsOn, endsOn, defaultTarget, rules, Now);

    private static List<NutritionTargetDayRuleSpec> AllDefault() =>
        NutritionTargetPlan.Week.Select(day => new NutritionTargetDayRuleSpec(day, NutritionTargetDayMode.Default)).ToList();

    // Mon/Wed/Fri 2500, Sun 2800 (or no target), other days the default.
    internal static List<NutritionTargetDayRuleSpec> TrainingWeek(bool sundayNoTarget = false) =>
        NutritionTargetPlan.Week.Select(day => day switch
        {
            DayOfWeek.Monday or DayOfWeek.Wednesday or DayOfWeek.Friday => new NutritionTargetDayRuleSpec(day, NutritionTargetDayMode.Custom, Training),
            DayOfWeek.Sunday => sundayNoTarget
                ? new NutritionTargetDayRuleSpec(day, NutritionTargetDayMode.NoTarget)
                : new NutritionTargetDayRuleSpec(day, NutritionTargetDayMode.Custom, Sunday),
            _ => new NutritionTargetDayRuleSpec(day, NutritionTargetDayMode.Default)
        }).ToList();

    private static decimal? Parse(string? value) => value is null ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
