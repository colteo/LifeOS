namespace LifeOS.Domain.Nutrition.Hydration;

// HYD-001 §5: the target a date had, from one user's target versions. Pure: no clock, no storage.
public sealed class HydrationTargetSchedule
{
    // By effective date; on equal dates the last one given wins (the stored row replaces the others).
    private readonly IReadOnlyList<HydrationTargetVersion> _versions;

    public HydrationTargetSchedule(IEnumerable<HydrationTargetVersion> versions)
    {
        _versions = versions
            .GroupBy(version => version.EffectiveFrom)
            .Select(group => group.Last())
            .OrderBy(version => version.EffectiveFrom)
            .ToList();
    }

    // The latest version with EffectiveFrom <= date; null before the first version (never the first value).
    public HydrationTargetVersion? VersionOn(DateOnly date) =>
        _versions.LastOrDefault(version => version.EffectiveFrom <= date);

    public int? TargetOn(DateOnly date) => VersionOn(date)?.TargetMl;

    public HydrationDay Day(DateOnly date, int totalMl) => new(date, totalMl, TargetOn(date));
}
