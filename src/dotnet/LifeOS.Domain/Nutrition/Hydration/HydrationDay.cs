namespace LifeOS.Domain.Nutrition.Hydration;

// One diary day of water (HYD-001 §5): computed on every read, never persisted. This is the only place
// that decides whether a day's target is met, for Hydration and for every module reading it.
public sealed record HydrationDay
{
    public HydrationDay(DateOnly date, int totalMl, int? targetMl)
    {
        if (totalMl < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalMl), "A day's total cannot be negative.");
        }

        if (targetMl is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetMl), "A target must be more than 0 ml.");
        }

        Date = date;
        TotalMl = totalMl;
        TargetMl = targetMl;
    }

    public DateOnly Date { get; }

    public int TotalMl { get; }

    // Null: no target on this date.
    public int? TargetMl { get; }

    // Over the target is still met; without a target a day is never met, whatever the total.
    public bool IsTargetMet => TargetMl is { } target && TotalMl >= target;

    // floor(total × 100 / target); may exceed 100. Null without a target.
    public int? ProgressPercent => TargetMl is { } target ? (int)((long)TotalMl * 100 / target) : null;
}
