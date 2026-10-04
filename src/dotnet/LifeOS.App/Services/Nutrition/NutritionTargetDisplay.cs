using System.Globalization;
using LifeOS.Contracts.Nutrition;

namespace LifeOS.App.Services.Nutrition;

// Presentation rules of manual daily targets (NUT-003). Plain .NET, no MAUI. The API is authoritative
// for the values and for which day a change applies to. Consumed values are compared with targets as
// plain numbers ("1840 / 2200 kcal"); nothing here judges the difference.
public static class NutritionTargetDisplay
{
	public const string ChangesApplyFromToday = "Changes apply from today.";
	public const string EnterAtLeastOne = "Enter at least one target.";
	public const string UseRemoveInstead = "Enter at least one target. To have no targets, use Remove targets.";
	public const string MustBePositive = "Targets must be more than 0.";
	public const string RemoveConfirmation = "Remove daily targets from today? Earlier days keep their targets.";

	private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

	// "2200 kcal", or null when calories are not targeted.
	public static string? Kcal(NutritionTargetResponse target) =>
		target.CaloriesKcal is { } calories ? NutritionDisplay.Kcal(calories) : null;

	// "160 P · 240 C · 70 F" with only the targeted macros, or null when none is targeted.
	public static string? Macros(NutritionTargetResponse target)
	{
		var parts = new List<string>();
		Add(parts, target.ProteinGrams, "P");
		Add(parts, target.CarbsGrams, "C");
		Add(parts, target.FatGrams, "F");

		return parts.Count == 0 ? null : string.Join(" · ", parts);
	}

	// "Effective from 4 October" (with the year when it is not the current one).
	public static string EffectiveFrom(DateOnly effectiveFrom, DateOnly today) =>
		"Effective from " + effectiveFrom.ToString(effectiveFrom.Year == today.Year ? "d MMMM" : "d MMMM yyyy", Culture);

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

	// Convenience checks before saving; the API validates everything. Null when the form can be sent.
	public static string? Validate(decimal? calories, decimal? protein, decimal? carbs, decimal? fat, bool hasTarget)
	{
		if (calories is null && protein is null && carbs is null && fat is null)
		{
			return hasTarget ? UseRemoveInstead : EnterAtLeastOne;
		}

		return calories <= 0 || protein <= 0 || carbs <= 0 || fat <= 0 ? MustBePositive : null;
	}

	public static SetNutritionTargetRequest Request(decimal? calories, decimal? protein, decimal? carbs, decimal? fat, int utcOffsetMinutes) =>
		new(calories, protein, carbs, fat, utcOffsetMinutes);

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
