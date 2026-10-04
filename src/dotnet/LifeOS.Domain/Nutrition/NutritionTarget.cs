namespace LifeOS.Domain.Nutrition;

// One state in a user's daily-target history (NUT-003), effective from EffectiveFrom until the next
// state. The target that applies to a day is the latest state with EffectiveFrom on or before it;
// targets are never copied onto days and past states are never rewritten or deleted.
//
// A state either carries targets (at least one metric) or explicitly means "no targets" (every metric
// null), which is how Remove targets ends a target without erasing history. There is at most one state
// per user and EffectiveFrom: saving again on the same day replaces that day's state.
public sealed class NutritionTarget
{
    private NutritionTarget() { }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public decimal? CaloriesKcal { get; private set; }

    public decimal? ProteinGrams { get; private set; }

    public decimal? CarbsGrams { get; private set; }

    public decimal? FatGrams { get; private set; }

    public NutritionTargetSource Source { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public bool HasTargets => CaloriesKcal is not null || ProteinGrams is not null || CarbsGrams is not null || FatGrams is not null;

    // The targets of this state, or null for a "no targets" state.
    public NutritionTargetValues? Values => HasTargets ? NutritionTargetValues.Create(CaloriesKcal, ProteinGrams, CarbsGrams, FatGrams) : null;

    public static NutritionTarget Set(Guid userId, DateOnly effectiveFrom, NutritionTargetValues values, NutritionTargetSource source,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(values);

        var target = Create(userId, effectiveFrom, source, now);
        target.CaloriesKcal = values.CaloriesKcal;
        target.ProteinGrams = values.ProteinGrams;
        target.CarbsGrams = values.CarbsGrams;
        target.FatGrams = values.FatGrams;

        return target;
    }

    // "No targets" from effectiveFrom on; earlier states keep applying to their days.
    public static NutritionTarget Remove(Guid userId, DateOnly effectiveFrom, NutritionTargetSource source, DateTimeOffset now) =>
        Create(userId, effectiveFrom, source, now);

    private static NutritionTarget Create(Guid userId, DateOnly effectiveFrom, NutritionTargetSource source, DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        // Npgsql represents the DateOnly endpoints as PostgreSQL infinities, not calendar dates.
        if (effectiveFrom == DateOnly.MinValue || effectiveFrom == DateOnly.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveFrom), "Choose a calendar date between 0001-01-02 and 9999-12-30.");
        }

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), "Unknown target source.");
        }

        return new NutritionTarget
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            EffectiveFrom = effectiveFrom,
            Source = source,
            CreatedAtUtc = now.ToUniversalTime(),
            UpdatedAtUtc = now.ToUniversalTime()
        };
    }
}
