namespace LifeOS.Domain.Habits;

// A set of ISO weekdays stored as a bitmask: Monday 1, Tuesday 2, ... Sunday 64 (1..127 when not empty).
public readonly record struct WeekdaySet
{
    public const int AllMask = 127;

    public static readonly IReadOnlyList<DayOfWeek> Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private WeekdaySet(int mask) => Mask = mask;

    public int Mask { get; }

    public bool IsEmpty => Mask == 0;

    // Monday to Sunday.
    public IReadOnlyList<DayOfWeek> Days => Week.Where(Contains).ToList();

    public static WeekdaySet Of(params IEnumerable<DayOfWeek> days)
    {
        var mask = 0;

        foreach (var day in days)
        {
            if (!Enum.IsDefined(day))
            {
                throw new ArgumentOutOfRangeException(nameof(days), "Unknown weekday.");
            }

            mask |= Bit(day);
        }

        return new WeekdaySet(mask);
    }

    public static WeekdaySet FromMask(int mask)
    {
        if (mask is < 0 or > AllMask)
        {
            throw new ArgumentOutOfRangeException(nameof(mask), "A weekday mask is between 0 and 127.");
        }

        return new WeekdaySet(mask);
    }

    public bool Contains(DayOfWeek day) => (Mask & Bit(day)) != 0;

    // ISO weekday number: Monday 1 ... Sunday 7.
    public static int Iso(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    private static int Bit(DayOfWeek day) => 1 << (Iso(day) - 1);
}
