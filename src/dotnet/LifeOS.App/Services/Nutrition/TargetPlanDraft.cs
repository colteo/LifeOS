using LifeOS.Contracts.Nutrition;

namespace LifeOS.App.Services.Nutrition;

// Four optional target fields as typed in a form.
public sealed class TargetFields
{
	public decimal? CaloriesKcal { get; set; }

	public decimal? ProteinGrams { get; set; }

	public decimal? CarbsGrams { get; set; }

	public decimal? FatGrams { get; set; }

	public bool IsEmpty => CaloriesKcal is null && ProteinGrams is null && CarbsGrams is null && FatGrams is null;

	public bool HasNonPositive => CaloriesKcal <= 0 || ProteinGrams <= 0 || CarbsGrams <= 0 || FatGrams <= 0;

	public NutritionTargetValuesDto? ToDto() => IsEmpty ? null : new(CaloriesKcal, ProteinGrams, CarbsGrams, FatGrams);

	public static TargetFields From(NutritionTargetValuesDto? target) => new()
	{
		CaloriesKcal = target?.CaloriesKcal,
		ProteinGrams = target?.ProteinGrams,
		CarbsGrams = target?.CarbsGrams,
		FatGrams = target?.FatGrams
	};
}

// One weekday row of the plan editor. Its fields are shown (and sent) only in Custom mode.
public sealed class TargetDayDraft(string weekday)
{
	public string Weekday { get; } = weekday;

	public string Mode { get; set; } = NutritionTargetDisplay.Default;

	public TargetFields Target { get; set; } = new();

	public bool IsCustom => Mode == NutritionTargetDisplay.Custom;
}

// The plan editor's state (NUT-003). Plain .NET: convenience checks only; the API validates everything
// and is authoritative for overlaps (409), including periods saved meanwhile on another device.
public sealed class TargetPlanDraft
{
	public static readonly IReadOnlyList<string> Weekdays = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

	public static readonly IReadOnlyList<string> Modes = [NutritionTargetDisplay.Default, NutritionTargetDisplay.Custom, NutritionTargetDisplay.NoTarget];

	private TargetPlanDraft(Guid? id) => Id = id;

	// Null for a new period.
	public Guid? Id { get; }

	public DateOnly? StartsOn { get; set; }

	public DateOnly? EndsOn { get; set; }

	public TargetFields Default { get; set; } = new();

	public IReadOnlyList<TargetDayDraft> Days { get; } = Weekdays.Select(day => new TargetDayDraft(day)).ToList();

	// A new period starts today and lasts four weeks; every weekday uses the default.
	public static TargetPlanDraft New(DateOnly today) => new(null) { StartsOn = today, EndsOn = today.AddDays(27) };

	public static TargetPlanDraft From(NutritionTargetPlanResponse plan)
	{
		var draft = new TargetPlanDraft(plan.Id)
		{
			StartsOn = plan.StartsOn,
			EndsOn = plan.EndsOn,
			Default = TargetFields.From(plan.DefaultTarget)
		};

		foreach (var rule in plan.WeeklyRules)
		{
			if (draft.Days.FirstOrDefault(day => day.Weekday == rule.Weekday) is { } day)
			{
				day.Mode = rule.Mode ?? NutritionTargetDisplay.Default;
				day.Target = TargetFields.From(rule.Target);
			}
		}

		return draft;
	}

	// The live overlap message as the dates change, or null.
	public string? OverlapError(IEnumerable<NutritionTargetPlanResponse> plans, DateOnly today) =>
		StartsOn is { } startsOn && EndsOn is { } endsOn && endsOn >= startsOn
		&& NutritionTargetDisplay.FindOverlap(plans, startsOn, endsOn, Id) is { } conflict
			? NutritionTargetDisplay.OverlapMessage(conflict, today)
			: null;

	public IReadOnlyList<string> Validate(IEnumerable<NutritionTargetPlanResponse> plans, DateOnly today)
	{
		var errors = new List<string>();

		if (StartsOn is null || EndsOn is null)
		{
			errors.Add("Choose the first and last day of the period.");
		}
		else if (EndsOn < StartsOn)
		{
			errors.Add("The period must end on or after its first day.");
		}
		else if (OverlapError(plans, today) is { } overlap)
		{
			errors.Add(overlap);
		}

		if (Default.HasNonPositive || Days.Any(day => day.IsCustom && day.Target.HasNonPositive))
		{
			errors.Add("Targets must be more than 0.");
		}

		foreach (var day in Days)
		{
			if (day.Mode == NutritionTargetDisplay.Default && Default.IsEmpty)
			{
				errors.Add($"{day.Weekday} uses the default target, but the period has none.");
			}
			else if (day.IsCustom && day.Target.IsEmpty)
			{
				errors.Add($"{day.Weekday}: enter at least one custom target.");
			}
		}

		return errors;
	}

	// Custom fields of non-custom days are never sent.
	public NutritionTargetPlanRequest ToRequest() => new(StartsOn, EndsOn, Default.ToDto(),
		Days.Select(day => new NutritionTargetDayRuleDto(day.Weekday, day.Mode, day.IsCustom ? day.Target.ToDto() : null)).ToList());
}
