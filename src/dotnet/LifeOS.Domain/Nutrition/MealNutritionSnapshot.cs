namespace LifeOS.Domain.Nutrition;

// The current nutrition of one meal (NUT-002): at most one per MealEntry, stored apart from the meal.
// It describes the meal's description as it was when the snapshot was made: changing the description
// removes the snapshot, while changing only the time or meal type keeps it.
//
// Replacement rule: an explicit user decision (AiConfirmed, UserAdjusted) may replace the current
// snapshot. Bulk analysis (AiRequested) and lazy close (AiAutoClosed) only fill meals that have none,
// so they can never overwrite an existing, possibly user-adjusted, snapshot.
public sealed class MealNutritionSnapshot
{
    private MealNutritionSnapshot() { }

    public Guid Id { get; private set; }

    public Guid MealEntryId { get; private set; }

    public decimal CaloriesKcal { get; private set; }

    public decimal ProteinGrams { get; private set; }

    public decimal CarbsGrams { get; private set; }

    public decimal FatGrams { get; private set; }

    public NutritionSource Source { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public NutritionValues Values => NutritionValues.Create(CaloriesKcal, ProteinGrams, CarbsGrams, FatGrams);

    public static bool MayReplaceExisting(NutritionSource source) =>
        source is NutritionSource.AiConfirmed or NutritionSource.UserAdjusted;

    public static MealNutritionSnapshot Create(Guid mealEntryId, NutritionValues values, NutritionSource source, DateTimeOffset now)
    {
        if (mealEntryId == Guid.Empty)
        {
            throw new ArgumentException("A valid meal id is required.", nameof(mealEntryId));
        }

        var snapshot = new MealNutritionSnapshot
        {
            Id = Guid.CreateVersion7(),
            MealEntryId = mealEntryId,
            CreatedAtUtc = now.ToUniversalTime()
        };

        snapshot.Apply(values, source, now);

        return snapshot;
    }

    public void Replace(NutritionValues values, NutritionSource source, DateTimeOffset now)
    {
        if (!MayReplaceExisting(source))
        {
            throw new InvalidOperationException($"{source} nutrition never replaces an existing snapshot.");
        }

        Apply(values, source, now);
    }

    private void Apply(NutritionValues values, NutritionSource source, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Unknown nutrition source.");
        }

        CaloriesKcal = values.CaloriesKcal;
        ProteinGrams = values.ProteinGrams;
        CarbsGrams = values.CarbsGrams;
        FatGrams = values.FatGrams;
        Source = source;
        UpdatedAtUtc = now.ToUniversalTime();
    }
}
