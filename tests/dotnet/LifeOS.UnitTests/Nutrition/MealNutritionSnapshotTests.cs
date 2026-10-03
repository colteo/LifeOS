using LifeOS.Domain.Nutrition;

namespace LifeOS.UnitTests.Nutrition;

public class MealNutritionSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid Meal = Guid.CreateVersion7();

    [Fact]
    public void Values_AreKeptToOneDecimal_AwayFromZero()
    {
        var values = NutritionValues.Create(620.04m, 52.25m, 58m, 0m);

        Assert.Equal((620.0m, 52.3m, 58.0m, 0m), (values.CaloriesKcal, values.ProteinGrams, values.CarbsGrams, values.FatGrams));
    }

    [Theory]
    [InlineData(-1, 0, 0, 0, "caloriesKcal")]
    [InlineData(0, -0.1, 0, 0, "proteinGrams")]
    [InlineData(0, 0, -5, 0, "carbsGrams")]
    [InlineData(0, 0, 0, -0.01, "fatGrams")]
    [InlineData(10000.1, 0, 0, 0, "caloriesKcal")]
    [InlineData(0, 1000.1, 0, 0, "proteinGrams")]
    [InlineData(0, 0, 1001, 0, "carbsGrams")]
    [InlineData(0, 0, 0, 2000, "fatGrams")]
    public void Values_AreNonNegativeAndBounded(double calories, double protein, double carbs, double fat, string field)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() =>
            NutritionValues.Create((decimal)calories, (decimal)protein, (decimal)carbs, (decimal)fat));

        Assert.Equal(field, exception.ParamName);
    }

    [Fact]
    public void Values_AcceptTheBounds()
    {
        var values = NutritionValues.Create(NutritionValues.MaxCaloriesKcal, NutritionValues.MaxMacroGrams, 0, 1000.04m);

        Assert.Equal((10000m, 1000m, 1000.0m), (values.CaloriesKcal, values.ProteinGrams, values.FatGrams));
    }

    [Theory]
    [InlineData(NutritionSource.AiConfirmed)]
    [InlineData(NutritionSource.AiRequested)]
    [InlineData(NutritionSource.AiAutoClosed)]
    [InlineData(NutritionSource.UserAdjusted)]
    public void Create_IsValidForEveryDefinedSource(NutritionSource source)
    {
        var snapshot = MealNutritionSnapshot.Create(Meal, NutritionValues.Create(620, 52, 58, 20), source, Now.ToOffset(TimeSpan.FromHours(2)));

        Assert.NotEqual(Guid.Empty, snapshot.Id);
        Assert.Equal(7, snapshot.Id.Version);
        Assert.Equal(Meal, snapshot.MealEntryId);
        Assert.Equal(source, snapshot.Source);
        Assert.Equal((620m, 52m, 58m, 20m), (snapshot.CaloriesKcal, snapshot.ProteinGrams, snapshot.CarbsGrams, snapshot.FatGrams));
        Assert.Equal(NutritionValues.Create(620, 52, 58, 20), snapshot.Values);
        Assert.Equal((Now, Now, TimeSpan.Zero), (snapshot.CreatedAtUtc, snapshot.UpdatedAtUtc, snapshot.CreatedAtUtc.Offset));
    }

    [Fact]
    public void Create_RejectsAnUndefinedSourceAndAMissingMeal()
    {
        var values = NutritionValues.Create(1, 1, 1, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => MealNutritionSnapshot.Create(Meal, values, (NutritionSource)42, Now));
        Assert.Throws<ArgumentException>(() => MealNutritionSnapshot.Create(Guid.Empty, values, NutritionSource.AiConfirmed, Now));
        Assert.Throws<ArgumentNullException>(() => MealNutritionSnapshot.Create(Meal, null!, NutritionSource.AiConfirmed, Now));
    }

    [Theory]
    [InlineData(NutritionSource.AiConfirmed, true)]
    [InlineData(NutritionSource.UserAdjusted, true)]
    [InlineData(NutritionSource.AiRequested, false)]
    [InlineData(NutritionSource.AiAutoClosed, false)]
    public void OnlyExplicitDecisions_MayReplaceAnExistingSnapshot(NutritionSource source, bool mayReplace)
    {
        Assert.Equal(mayReplace, MealNutritionSnapshot.MayReplaceExisting(source));
    }

    [Fact]
    public void Replace_WithUserAdjusted_UpdatesValuesSourceAndTime_KeepsIdentityAndCreation()
    {
        var snapshot = MealNutritionSnapshot.Create(Meal, NutritionValues.Create(620, 52, 58, 20), NutritionSource.AiAutoClosed, Now);

        snapshot.Replace(NutritionValues.Create(700, 60, 50, 25), NutritionSource.UserAdjusted, Now.AddHours(1));

        Assert.Equal((700m, 60m, 50m, 25m), (snapshot.CaloriesKcal, snapshot.ProteinGrams, snapshot.CarbsGrams, snapshot.FatGrams));
        Assert.Equal(NutritionSource.UserAdjusted, snapshot.Source);
        Assert.Equal((Now, Now.AddHours(1)), (snapshot.CreatedAtUtc, snapshot.UpdatedAtUtc));
        Assert.Equal(Meal, snapshot.MealEntryId);
    }

    [Fact]
    public void Replace_WithAConfirmedEstimate_ReplacesAUserAdjustedSnapshot()
    {
        var snapshot = MealNutritionSnapshot.Create(Meal, NutritionValues.Create(700, 60, 50, 25), NutritionSource.UserAdjusted, Now);

        snapshot.Replace(NutritionValues.Create(640, 50, 60, 21), NutritionSource.AiConfirmed, Now.AddHours(1));

        Assert.Equal((640m, NutritionSource.AiConfirmed), (snapshot.CaloriesKcal, snapshot.Source));
    }

    [Theory]
    [InlineData(NutritionSource.AiRequested)]
    [InlineData(NutritionSource.AiAutoClosed)]
    public void Replace_WithBulkSources_IsRefused_AndChangesNothing(NutritionSource source)
    {
        var snapshot = MealNutritionSnapshot.Create(Meal, NutritionValues.Create(700, 60, 50, 25), NutritionSource.UserAdjusted, Now);

        Assert.Throws<InvalidOperationException>(() => snapshot.Replace(NutritionValues.Create(1, 1, 1, 1), source, Now.AddHours(1)));

        Assert.Equal((700m, NutritionSource.UserAdjusted, Now), (snapshot.CaloriesKcal, snapshot.Source, snapshot.UpdatedAtUtc));
    }
}
