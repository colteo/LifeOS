namespace LifeOS.Domain.Nutrition.Hydration;

// HYD-001 P-4/P-5: the daily water target in effect from EffectiveFrom until the next version starts.
// Versions are open-ended, so past days keep the target they had. TargetMl null is an explicit "no
// target from this date". Identity = UserId + EffectiveFrom (saving again on the same date replaces it).
public sealed class HydrationTargetVersion
{
    public const int MinTargetMl = 250;
    public const int MaxTargetMl = 10000;

    private HydrationTargetVersion() { }

    public Guid UserId { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public int? TargetMl { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static HydrationTargetVersion Create(Guid userId, DateOnly effectiveFrom, int? targetMl, DateTimeOffset now)
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

        if (targetMl is < MinTargetMl or > MaxTargetMl)
        {
            throw new ArgumentOutOfRangeException(nameof(targetMl), $"Enter a daily target between {MinTargetMl} and {MaxTargetMl} ml.");
        }

        return new HydrationTargetVersion
        {
            UserId = userId,
            EffectiveFrom = effectiveFrom,
            TargetMl = targetMl,
            CreatedAtUtc = now.ToUniversalTime()
        };
    }
}
