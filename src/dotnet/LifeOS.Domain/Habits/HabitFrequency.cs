namespace LifeOS.Domain.Habits;

// How often a habit is expected (HAB-001 P-2). Weekdays carries a non-empty set; TimesPerWeek carries
// 1..7; Daily and Inactive carry nothing. Equal values are equal frequencies (versions merge on that).
public sealed record HabitFrequency
{
    public const int MinTimesPerWeek = 1;
    public const int MaxTimesPerWeek = 7;

    private HabitFrequency(HabitFrequencyKind kind, WeekdaySet? weekdays, int? timesPerWeek)
    {
        Kind = kind;
        Weekdays = weekdays;
        TimesPerWeek = timesPerWeek;
    }

    public static HabitFrequency Daily { get; } = new(HabitFrequencyKind.Daily, null, null);

    public static HabitFrequency Inactive { get; } = new(HabitFrequencyKind.Inactive, null, null);

    public HabitFrequencyKind Kind { get; }

    public WeekdaySet? Weekdays { get; }

    public int? TimesPerWeek { get; }

    public bool IsWeekBased => Kind == HabitFrequencyKind.TimesPerWeek;

    public bool IsDayBased => Kind is HabitFrequencyKind.Daily or HabitFrequencyKind.Weekdays;

    public static HabitFrequency OnWeekdays(WeekdaySet weekdays)
    {
        if (weekdays.IsEmpty)
        {
            throw new ArgumentException("Choose at least one day of the week.", "frequency.weekdays");
        }

        return new(HabitFrequencyKind.Weekdays, weekdays, null);
    }

    public static HabitFrequency PerWeek(int timesPerWeek)
    {
        if (timesPerWeek is < MinTimesPerWeek or > MaxTimesPerWeek)
        {
            throw new ArgumentOutOfRangeException("frequency.timesPerWeek", $"Choose between {MinTimesPerWeek} and {MaxTimesPerWeek} times per week.");
        }

        return new(HabitFrequencyKind.TimesPerWeek, null, timesPerWeek);
    }

    // The stored shape back to a frequency; throws when the shape is invalid.
    public static HabitFrequency From(HabitFrequencyKind kind, int? weekdaysMask, int? timesPerWeek) => kind switch
    {
        HabitFrequencyKind.Daily when weekdaysMask is null && timesPerWeek is null => Daily,
        HabitFrequencyKind.Inactive when weekdaysMask is null && timesPerWeek is null => Inactive,
        HabitFrequencyKind.Weekdays when weekdaysMask is { } mask && timesPerWeek is null => OnWeekdays(WeekdaySet.FromMask(mask)),
        HabitFrequencyKind.TimesPerWeek when timesPerWeek is { } times && weekdaysMask is null => PerWeek(times),
        _ => throw new ArgumentException("The frequency's fields do not match its kind.", "frequency")
    };

    // Whether the date is an expected day. TimesPerWeek has no fixed days; Inactive has none.
    public bool IsScheduledOn(DateOnly date) => Kind switch
    {
        HabitFrequencyKind.Daily => true,
        HabitFrequencyKind.Weekdays => Weekdays!.Value.Contains(date.DayOfWeek),
        _ => false
    };
}
