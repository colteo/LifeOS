namespace LifeOS.Domain.Habits;

// One period of a habit's schedule (HAB-001 P-6): the frequency in effect from EffectiveFrom until the
// next version starts. Persisted as rows owned by the habit; identity = HabitId + EffectiveFrom.
public sealed class HabitScheduleVersion
{
    private HabitScheduleVersion() { }

    internal HabitScheduleVersion(Guid habitId, Guid userId, DateOnly effectiveFrom, HabitFrequency frequency)
    {
        HabitId = habitId;
        UserId = userId;
        EffectiveFrom = effectiveFrom;
        Apply(frequency);
    }

    public Guid HabitId { get; private set; }

    public Guid UserId { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public HabitFrequencyKind Kind { get; private set; }

    // ISO bitmask (Monday 1 ... Sunday 64); only for Weekdays.
    public int? WeekdaysMask { get; private set; }

    // 1..7; only for TimesPerWeek.
    public int? TimesPerWeek { get; private set; }

    public HabitFrequency Frequency => HabitFrequency.From(Kind, WeekdaysMask, TimesPerWeek);

    internal void Apply(HabitFrequency frequency)
    {
        Kind = frequency.Kind;
        WeekdaysMask = frequency.Weekdays?.Mask;
        TimesPerWeek = frequency.TimesPerWeek;
    }
}
