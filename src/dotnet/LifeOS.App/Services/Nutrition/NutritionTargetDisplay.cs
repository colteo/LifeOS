using System.Globalization;
using LifeOS.Contracts.Nutrition;

namespace LifeOS.App.Services.Nutrition;

// Presentation rules of nutrition targets (NUT-003). Plain .NET, no MAUI. The API is authoritative for
// plans, overlaps and the resolved target of a day; these only decide wording. Consumed values are
// compared with targets as plain numbers ("1840 / 2200 kcal"); nothing here judges the difference.
public static class NutritionTargetDisplay
{
	public const string Default = "Default";
	public const string Custom = "Custom";
	public const string NoTarget = "NoTarget";

	public const string NoPeriodForDay = "No target period covers this day.";
	public const string NoTargetForDay = "No target for this day";

	private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

	// ---- Values ----

	// "2200 kcal", or null when calories are not targeted.
	public static string? Kcal(NutritionTargetValuesDto target) =>
		target.CaloriesKcal is { } calories ? NutritionDisplay.Kcal(calories) : null;

	// "160 P · 240 C · 70 F" with only the targeted macros, or null when none is targeted.
	public static string? Macros(NutritionTargetValuesDto target)
	{
		var parts = new List<string>();
		Add(parts, target.ProteinGrams, "P");
		Add(parts, target.CarbsGrams, "C");
		Add(parts, target.FatGrams, "F");

		return parts.Count == 0 ? null : string.Join(" · ", parts);
	}

	// One line: "2200 kcal · 160 P · 70 F".
	public static string Summary(NutritionTargetValuesDto target) =>
		string.Join(" · ", new[] { Kcal(target), Macros(target) }.Where(part => part is not null));

	// ---- Day comparison ----

	// "1840 / 2200 kcal", or "1840 kcal" when calories are not targeted.
	public static string KcalLine(decimal consumed, decimal? target) =>
		target is { } goal ? $"{NutritionDisplay.Whole(consumed)} / {NutritionDisplay.Whole(goal)} kcal" : NutritionDisplay.Kcal(consumed);

	// "138 / 160 g", or "138 g" when the macro is not targeted.
	public static string GramsLine(decimal consumed, decimal? target) =>
		target is { } goal ? $"{NutritionDisplay.Whole(consumed)} / {NutritionDisplay.Whole(goal)} g" : $"{NutritionDisplay.Whole(consumed)} g";

	// The compact macro line: "138 / 160 P · 191 C · 61 / 70 F". Without a target it is the NUT-002 line.
	public static string MacrosLine(DailyNutritionSummaryResponse summary) =>
		$"{Compare(summary.ProteinGrams, summary.Target?.ProteinGrams)} P · {Compare(summary.CarbsGrams, summary.Target?.CarbsGrams)} C · " +
		$"{Compare(summary.FatGrams, summary.Target?.FatGrams)} F";

	// ---- Periods ----

	// "7 Oct – 3 Nov" in the current year; otherwise with years ("2 Dec 2026 – 5 Jan 2027").
	public static string PeriodLabel(DateOnly startsOn, DateOnly endsOn, DateOnly today)
	{
		if (startsOn.Year == today.Year && endsOn.Year == today.Year)
		{
			return $"{startsOn.ToString("d MMM", Culture)} – {endsOn.ToString("d MMM", Culture)}";
		}

		return startsOn.Year == endsOn.Year
			? $"{startsOn.ToString("d MMM", Culture)} – {endsOn.ToString("d MMM yyyy", Culture)}"
			: $"{startsOn.ToString("d MMM yyyy", Culture)} – {endsOn.ToString("d MMM yyyy", Culture)}";
	}

	public static TargetPeriodStatus StatusOf(NutritionTargetPlanResponse plan, DateOnly today) =>
		plan.EndsOn < today ? TargetPeriodStatus.Past
		: plan.StartsOn > today ? TargetPeriodStatus.Upcoming
		: TargetPeriodStatus.Current;

	// "2200 kcal default", "160 P default" or "No default target".
	public static string DefaultLine(NutritionTargetPlanResponse plan) =>
		plan.DefaultTarget is { } target ? $"{Kcal(target) ?? Macros(target)} default" : "No default target";

	// The weekdays that differ from the default: "Mon · Wed · Fri custom", "Sun no target".
	public static IReadOnlyList<string> WeekLines(NutritionTargetPlanResponse plan)
	{
		var lines = new List<string>();
		AddDays(lines, plan, Custom, "custom");
		AddDays(lines, plan, NoTarget, "no target");

		return lines;
	}

	public static string ModeLabel(string mode) => mode switch
	{
		Custom => "Custom",
		NoTarget => "No target",
		_ => "Default"
	};

	public static string ShortDay(string weekday) => weekday.Length >= 3 ? weekday[..3] : weekday;

	// The first other period overlapping startsOn..endsOn (inclusive; adjacent periods do not overlap).
	public static NutritionTargetPlanResponse? FindOverlap(IEnumerable<NutritionTargetPlanResponse> plans, DateOnly startsOn, DateOnly endsOn,
		Guid? excludingId) =>
		plans.Where(plan => plan.Id != excludingId && plan.StartsOn <= endsOn && startsOn <= plan.EndsOn)
			.OrderBy(plan => plan.StartsOn)
			.FirstOrDefault();

	// "This period overlaps 7 Oct – 3 Nov."
	public static string OverlapMessage(NutritionTargetPlanResponse conflict, DateOnly today) =>
		$"This period overlaps {PeriodLabel(conflict.StartsOn, conflict.EndsOn, today)}.";

	public static string DeleteConfirmation(NutritionTargetPlanResponse plan, DateOnly today) =>
		$"Delete {PeriodLabel(plan.StartsOn, plan.EndsOn, today)}? Its days will have no target. Meals are not affected.";

	private static void AddDays(List<string> lines, NutritionTargetPlanResponse plan, string mode, string label)
	{
		var days = plan.WeeklyRules.Where(rule => rule.Mode == mode).Select(rule => ShortDay(rule.Weekday ?? "")).ToList();

		if (days.Count > 0)
		{
			lines.Add($"{string.Join(" · ", days)} {label}");
		}
	}

	private static string Compare(decimal consumed, decimal? target) =>
		target is { } goal ? $"{NutritionDisplay.Whole(consumed)} / {NutritionDisplay.Whole(goal)}" : NutritionDisplay.Whole(consumed);

	private static void Add(List<string> parts, decimal? grams, string letter)
	{
		if (grams is { } value)
		{
			parts.Add($"{NutritionDisplay.Whole(value)} {letter}");
		}
	}
}

public enum TargetPeriodStatus
{
	Current,
	Upcoming,
	Past
}
