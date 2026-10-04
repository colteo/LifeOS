using LifeOS.Domain.Nutrition;

namespace LifeOS.UnitTests.Nutrition;

// NUT-003 domain: daily target values and target states.
public class NutritionTargetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 4);
    private static readonly Guid User = Guid.CreateVersion7();

    [Fact]
    public void AllFourTargets_AreKept()
    {
        var values = NutritionTargetValues.Create(2200, 160, 240, 70);

        Assert.Equal((2200m, 160m, 240m, 70m), (values.CaloriesKcal, values.ProteinGrams, values.CarbsGrams, values.FatGrams));
    }

    [Theory]
    [InlineData(2200, null, null, null)]
    [InlineData(null, 160, null, null)]
    [InlineData(2200, 160, null, null)]
    [InlineData(null, null, 240, 70)]
    public void PartialTargets_LeaveUntargetedMetricsNull(int? kcal, int? protein, int? carbs, int? fat)
    {
        var values = NutritionTargetValues.Create(kcal, protein, carbs, fat);

        Assert.Equal(((decimal?)kcal, (decimal?)protein, (decimal?)carbs, (decimal?)fat),
            (values.CaloriesKcal, values.ProteinGrams, values.CarbsGrams, values.FatGrams));
    }

    [Fact]
    public void NoMetricAtAll_IsRejected_RemoveIsTheWayToHaveNone()
    {
        var exception = Assert.Throws<ArgumentException>(() => NutritionTargetValues.Create(null, null, null, null));

        Assert.StartsWith(NutritionTargetValues.NoneMessage, exception.Message);
    }

    [Theory]
    [InlineData("0", null, "caloriesKcal")]
    [InlineData(null, "0", "proteinGrams")]
    [InlineData("-1", null, "caloriesKcal")]
    [InlineData(null, "-0.1", "proteinGrams")]
    [InlineData("0.04", null, "caloriesKcal")]
    [InlineData("10000.1", null, "caloriesKcal")]
    [InlineData(null, "1000.06", "proteinGrams")]
    public void ZeroNegativeAndTooHigh_AreRejected_NamingTheField(string? kcal, string? protein, string field)
    {
        var exception = Assert.ThrowsAny<ArgumentOutOfRangeException>(() =>
            NutritionTargetValues.Create(Parse(kcal), Parse(protein), null, null));

        Assert.Equal(field, exception.ParamName);
    }

    [Theory]
    [InlineData("carbs")]
    [InlineData("fat")]
    public void EveryMacro_IsBoundedAndPositive(string macro)
    {
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() =>
            macro == "carbs" ? NutritionTargetValues.Create(null, null, 1000.1m, null) : NutritionTargetValues.Create(null, null, null, 0m));
    }

    [Fact]
    public void UpperBounds_AreInclusive()
    {
        var values = NutritionTargetValues.Create(10000, 1000, 1000, 1000);

        Assert.Equal((10000m, 1000m, 1000m, 1000m), (values.CaloriesKcal, values.ProteinGrams, values.CarbsGrams, values.FatGrams));
    }

    [Fact]
    public void Values_AreRoundedToOneDecimal_HalfAwayFromZero()
    {
        var values = NutritionTargetValues.Create(2199.95m, 160.04m, 0.05m, null);

        Assert.Equal((2200.0m, 160.0m, 0.1m), (values.CaloriesKcal, values.ProteinGrams, values.CarbsGrams));
    }

    [Fact]
    public void Set_IsAManualStateWithTargets_EffectiveFromTheGivenDay()
    {
        var target = NutritionTarget.Set(User, Today, NutritionTargetValues.Create(null, 160, null, null), NutritionTargetSource.Manual, Now);

        Assert.True(target.HasTargets);
        Assert.Equal(Today, target.EffectiveFrom);
        Assert.Equal(NutritionTargetSource.Manual, target.Source);
        Assert.Equal(160m, target.Values!.ProteinGrams);
        Assert.Null(target.Values.CaloriesKcal);
        Assert.Equal(7, target.Id.Version);
        Assert.Equal((Now, Now), (target.CreatedAtUtc, target.UpdatedAtUtc));
    }

    [Fact]
    public void Remove_IsAnExplicitNoTargetsState()
    {
        var target = NutritionTarget.Remove(User, Today, NutritionTargetSource.Manual, Now);

        Assert.False(target.HasTargets);
        Assert.Null(target.Values);
        Assert.Equal(Today, target.EffectiveFrom);
        Assert.Equal(NutritionTargetSource.Manual, target.Source);
        Assert.Equal((null, null, null, null), (target.CaloriesKcal, target.ProteinGrams, target.CarbsGrams, target.FatGrams));
    }

    [Fact]
    public void ManualIsTheOnlySourceToday()
    {
        Assert.Equal([NutritionTargetSource.Manual], Enum.GetValues<NutritionTargetSource>());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NutritionTarget.Remove(User, Today, (NutritionTargetSource)42, Now));
    }

    [Fact]
    public void AStateNeedsAUserAndACalendarDate()
    {
        var values = NutritionTargetValues.Create(2200, null, null, null);

        Assert.Throws<ArgumentException>(() => NutritionTarget.Set(Guid.Empty, Today, values, NutritionTargetSource.Manual, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => NutritionTarget.Set(User, DateOnly.MinValue, values, NutritionTargetSource.Manual, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => NutritionTarget.Remove(User, DateOnly.MaxValue, NutritionTargetSource.Manual, Now));
    }

    private static decimal? Parse(string? value) => value is null ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
