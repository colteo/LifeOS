namespace LifeOS.Domain.Nutrition;

// A per-date exception to a plan's weekday rule (NUT-003): a custom target or no target for that one
// date. It belongs to the plan covering the date (an override never creates a target outside a plan)
// and there is at most one per user and date. Removing it returns the date to its weekday rule.
public sealed class NutritionTargetOverride
{
    private NutritionTargetOverride() { }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid PlanId { get; private set; }

    public DateOnly Date { get; private set; }

    public NutritionTargetOverrideMode Mode { get; private set; }

    public decimal? CaloriesKcal { get; private set; }

    public decimal? ProteinGrams { get; private set; }

    public decimal? CarbsGrams { get; private set; }

    public decimal? FatGrams { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    // The custom target; null for NoTarget.
    public NutritionTargetValues? Target => NutritionTargetValues.FromStored(CaloriesKcal, ProteinGrams, CarbsGrams, FatGrams);

    public static NutritionTargetOverride Create(NutritionTargetPlan plan, DateOnly date, NutritionTargetOverrideMode mode,
        NutritionTargetValues? target, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.Covers(date))
        {
            throw new ArgumentOutOfRangeException(nameof(date), "No target period covers this date.");
        }

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Mode must be Custom or NoTarget.");
        }

        if (mode == NutritionTargetOverrideMode.Custom && target is null)
        {
            throw new ArgumentException("Enter at least one target for this day.", nameof(target));
        }

        if (mode == NutritionTargetOverrideMode.NoTarget && target is not null)
        {
            throw new ArgumentException("A day without a target has no values.", nameof(target));
        }

        return new NutritionTargetOverride
        {
            Id = Guid.CreateVersion7(),
            UserId = plan.UserId,
            PlanId = plan.Id,
            Date = date,
            Mode = mode,
            CaloriesKcal = target?.CaloriesKcal,
            ProteinGrams = target?.ProteinGrams,
            CarbsGrams = target?.CarbsGrams,
            FatGrams = target?.FatGrams,
            CreatedAtUtc = now.ToUniversalTime(),
            UpdatedAtUtc = now.ToUniversalTime()
        };
    }
}

// Deterministic target resolution for one diary date (NUT-003):
//   1. no plan covers the date        → no target
//   2. a daily override exists        → Custom: its target; NoTarget: no target
//   3. otherwise the weekday rule     → Default: plan default; Custom: weekday target; NoTarget: none
public static class NutritionTargetResolution
{
    public static NutritionTargetValues? Resolve(DateOnly date, NutritionTargetPlan? plan, NutritionTargetOverride? dailyOverride)
    {
        if (plan is null || !plan.Covers(date))
        {
            return null;
        }

        if (dailyOverride is not null && dailyOverride.Date == date && dailyOverride.PlanId == plan.Id)
        {
            return dailyOverride.Mode == NutritionTargetOverrideMode.Custom ? dailyOverride.Target : null;
        }

        return plan.WeekdayTarget(date);
    }
}
