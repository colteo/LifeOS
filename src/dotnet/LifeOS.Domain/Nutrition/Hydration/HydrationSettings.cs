namespace LifeOS.Domain.Nutrition.Hydration;

// HYD-001 P-6: a user's three quick-add amounts. At most one row per user; no row means the defaults
// (150 / 250 / 500 ml), so no backfill is ever needed. Duplicates are allowed.
public sealed class HydrationSettings
{
    public const int QuickAddCount = 3;

    public static readonly IReadOnlyList<int> DefaultQuickAddMl = [150, 250, 500];

    private HydrationSettings() { }

    public Guid UserId { get; private set; }

    public int QuickAdd1Ml { get; private set; }

    public int QuickAdd2Ml { get; private set; }

    public int QuickAdd3Ml { get; private set; }

    // DateTimeOffset.MinValue for the defaults, which are never saved.
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public IReadOnlyList<int> QuickAddMl => [QuickAdd1Ml, QuickAdd2Ml, QuickAdd3Ml];

    public static HydrationSettings Default(Guid userId)
    {
        RequireUser(userId);

        return new HydrationSettings
        {
            UserId = userId,
            QuickAdd1Ml = DefaultQuickAddMl[0],
            QuickAdd2Ml = DefaultQuickAddMl[1],
            QuickAdd3Ml = DefaultQuickAddMl[2],
            UpdatedAtUtc = DateTimeOffset.MinValue
        };
    }

    // Exactly three amounts, each a valid intake amount. The parameter name of a bad amount is its
    // request field, e.g. "amountsMl[1]".
    public static HydrationSettings Create(Guid userId, IReadOnlyList<int> quickAddMl, DateTimeOffset now)
    {
        RequireUser(userId);
        ArgumentNullException.ThrowIfNull(quickAddMl);

        if (quickAddMl.Count != QuickAddCount)
        {
            throw new ArgumentException($"Choose exactly {QuickAddCount} quick-add amounts.", "amountsMl");
        }

        for (var i = 0; i < quickAddMl.Count; i++)
        {
            if (quickAddMl[i] is < HydrationIntake.MinAmountMl or > HydrationIntake.MaxAmountMl)
            {
                throw new ArgumentOutOfRangeException($"amountsMl[{i}]",
                    $"Enter an amount between {HydrationIntake.MinAmountMl} and {HydrationIntake.MaxAmountMl} ml.");
            }
        }

        return new HydrationSettings
        {
            UserId = userId,
            QuickAdd1Ml = quickAddMl[0],
            QuickAdd2Ml = quickAddMl[1],
            QuickAdd3Ml = quickAddMl[2],
            UpdatedAtUtc = now.ToUniversalTime()
        };
    }

    private static void RequireUser(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }
    }
}
