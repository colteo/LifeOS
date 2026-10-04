namespace LifeOS.Domain.Nutrition;

// The desired rule for one weekday when creating or editing a plan.
public sealed record NutritionTargetDayRuleSpec(DayOfWeek Weekday, NutritionTargetDayMode Mode, NutritionTargetValues? Target = null);

// A bounded nutrition target period (NUT-003): StartsOn..EndsOn inclusive, both required, with an
// optional default target and exactly one rule for every weekday (Default, Custom or NoTarget).
//
// Rules:
//   - EndsOn >= StartsOn; a one-day period is valid. There are no open-ended plans.
//   - The default target may be absent: a plan may define only custom/no-target weekdays. A weekday
//     set to Default is then invalid.
//   - Custom weekdays carry their own target (at least one metric); Default and NoTarget carry none.
//   - Plans of one user never overlap. That needs the other plans, so it is enforced by the
//     Application (readable conflict) and by the database (concurrency-safe), not here.
// Editing a plan is explicit configuration: dates it covers resolve with the edited rules, past
// dates included. Nothing is snapshotted per day.
public sealed class NutritionTargetPlan
{
    public static readonly IReadOnlyList<DayOfWeek> Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private readonly List<NutritionTargetDayRule> _rules = [];

    private NutritionTargetPlan() { }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public DateOnly StartsOn { get; private set; }

    public DateOnly EndsOn { get; private set; }

    public decimal? DefaultCaloriesKcal { get; private set; }

    public decimal? DefaultProteinGrams { get; private set; }

    public decimal? DefaultCarbsGrams { get; private set; }

    public decimal? DefaultFatGrams { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public NutritionTargetValues? DefaultTarget =>
        NutritionTargetValues.FromStored(DefaultCaloriesKcal, DefaultProteinGrams, DefaultCarbsGrams, DefaultFatGrams);

    // Monday to Sunday.
    public IReadOnlyList<NutritionTargetDayRule> Rules => _rules.OrderBy(rule => Iso(rule.Weekday)).ToList();

    public static NutritionTargetPlan Create(Guid userId, DateOnly startsOn, DateOnly endsOn, NutritionTargetValues? defaultTarget,
        IReadOnlyCollection<NutritionTargetDayRuleSpec> weeklyRules, DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        var plan = new NutritionTargetPlan
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CreatedAtUtc = now.ToUniversalTime()
        };

        plan._rules.AddRange(Week.Select(day => new NutritionTargetDayRule(day)));
        plan.Apply(startsOn, endsOn, defaultTarget, weeklyRules, now);

        return plan;
    }

    // Replaces the period, default and weekly pattern; the plan keeps its identity and its rule rows.
    public void Update(DateOnly startsOn, DateOnly endsOn, NutritionTargetValues? defaultTarget,
        IReadOnlyCollection<NutritionTargetDayRuleSpec> weeklyRules, DateTimeOffset now) =>
        Apply(startsOn, endsOn, defaultTarget, weeklyRules, now);

    public bool Covers(DateOnly date) => StartsOn <= date && date <= EndsOn;

    // Inclusive ranges: adjacent periods (one ends the day before the other starts) do not overlap.
    public bool Overlaps(DateOnly startsOn, DateOnly endsOn) => StartsOn <= endsOn && startsOn <= EndsOn;

    // The weekday rule for a covered date (ignoring daily overrides); null for "no target".
    public NutritionTargetValues? WeekdayTarget(DateOnly date)
    {
        if (!Covers(date))
        {
            throw new ArgumentOutOfRangeException(nameof(date), "The plan does not cover this date.");
        }

        var rule = _rules.Single(candidate => candidate.Weekday == date.DayOfWeek);

        return rule.Mode switch
        {
            NutritionTargetDayMode.Default => DefaultTarget,
            NutritionTargetDayMode.Custom => rule.Target,
            _ => null
        };
    }

    // ISO weekday number: Monday 1 ... Sunday 7 (how rules are stored).
    public static int Iso(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    public static DayOfWeek FromIso(int day) => day == 7 ? DayOfWeek.Sunday : (DayOfWeek)day;

    private void Apply(DateOnly startsOn, DateOnly endsOn, NutritionTargetValues? defaultTarget,
        IReadOnlyCollection<NutritionTargetDayRuleSpec> weeklyRules, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(weeklyRules);

        // Npgsql represents the DateOnly endpoints as PostgreSQL infinities, not calendar dates.
        if (startsOn == DateOnly.MinValue || startsOn == DateOnly.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(startsOn), "Choose a calendar date between 0001-01-02 and 9999-12-30.");
        }

        if (endsOn == DateOnly.MinValue || endsOn == DateOnly.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(endsOn), "Choose a calendar date between 0001-01-02 and 9999-12-30.");
        }

        if (endsOn < startsOn)
        {
            throw new ArgumentException("The period must end on or after its first day.", nameof(endsOn));
        }

        if (weeklyRules.Count != 7 || weeklyRules.Select(rule => rule.Weekday).Distinct().Count() != 7
            || weeklyRules.Any(rule => !Enum.IsDefined(rule.Weekday)))
        {
            throw new ArgumentException("Set one rule for every weekday, Monday to Sunday.", "weeklyRules");
        }

        foreach (var spec in weeklyRules)
        {
            if (spec.Mode == NutritionTargetDayMode.Default && defaultTarget is null)
            {
                throw new ArgumentException($"{spec.Weekday} uses the default target, but the period has none.", RuleField(spec.Weekday));
            }

            NutritionTargetDayRule.Validate(spec);
        }

        StartsOn = startsOn;
        EndsOn = endsOn;
        DefaultCaloriesKcal = defaultTarget?.CaloriesKcal;
        DefaultProteinGrams = defaultTarget?.ProteinGrams;
        DefaultCarbsGrams = defaultTarget?.CarbsGrams;
        DefaultFatGrams = defaultTarget?.FatGrams;

        foreach (var spec in weeklyRules)
        {
            _rules.Single(rule => rule.Weekday == spec.Weekday).Apply(spec);
        }

        UpdatedAtUtc = now.ToUniversalTime();
    }

    internal static string RuleField(DayOfWeek weekday) => $"weeklyRules.{weekday}";
}

// One weekday of a plan. Default and NoTarget carry no values; Custom carries at least one.
public sealed class NutritionTargetDayRule
{
    private NutritionTargetDayRule() { }

    internal NutritionTargetDayRule(DayOfWeek weekday)
    {
        Weekday = weekday;
        Mode = NutritionTargetDayMode.Default;
    }

    public DayOfWeek Weekday { get; private set; }

    public NutritionTargetDayMode Mode { get; private set; }

    public decimal? CaloriesKcal { get; private set; }

    public decimal? ProteinGrams { get; private set; }

    public decimal? CarbsGrams { get; private set; }

    public decimal? FatGrams { get; private set; }

    // The custom target; null for Default and NoTarget.
    public NutritionTargetValues? Target => NutritionTargetValues.FromStored(CaloriesKcal, ProteinGrams, CarbsGrams, FatGrams);

    internal static void Validate(NutritionTargetDayRuleSpec spec)
    {
        if (!Enum.IsDefined(spec.Mode))
        {
            throw new ArgumentOutOfRangeException(NutritionTargetPlan.RuleField(spec.Weekday), "Unknown weekday mode.");
        }

        if (spec.Mode == NutritionTargetDayMode.Custom && spec.Target is null)
        {
            throw new ArgumentException($"{spec.Weekday}: enter at least one custom target.", NutritionTargetPlan.RuleField(spec.Weekday));
        }

        if (spec.Mode != NutritionTargetDayMode.Custom && spec.Target is not null)
        {
            throw new ArgumentException($"{spec.Weekday}: only a custom day has its own target.", NutritionTargetPlan.RuleField(spec.Weekday));
        }
    }

    internal void Apply(NutritionTargetDayRuleSpec spec)
    {
        Mode = spec.Mode;
        CaloriesKcal = spec.Target?.CaloriesKcal;
        ProteinGrams = spec.Target?.ProteinGrams;
        CarbsGrams = spec.Target?.CarbsGrams;
        FatGrams = spec.Target?.FatGrams;
    }
}
