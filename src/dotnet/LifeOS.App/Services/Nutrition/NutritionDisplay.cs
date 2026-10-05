using System.Globalization;
using LifeOS.Contracts.Nutrition;

namespace LifeOS.App.Services.Nutrition;

// Presentation rules of meal nutrition and daily totals (NUT-002). Plain .NET, no MAUI. The API is
// authoritative for every value and total; these only decide wording and which action to offer.
public static class NutritionDisplay
{
	public const string AiConfirmed = "AiConfirmed";
	public const string UserAdjusted = "UserAdjusted";

	private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

	// Whole kcal and grams: the estimates are not precise enough for decimals.
	public static string Kcal(decimal calories) => $"{Whole(calories)} kcal";

	public static string Macros(decimal protein, decimal carbs, decimal fat) =>
		$"{Whole(protein)} P · {Whole(carbs)} C · {Whole(fat)} F";

	public static string Kcal(MealNutritionResponse nutrition) => Kcal(nutrition.CaloriesKcal);

	public static string Macros(MealNutritionResponse nutrition) => Macros(nutrition.ProteinGrams, nutrition.CarbsGrams, nutrition.FatGrams);

	// Concise provenance; never a provider or model name.
	public static string SourceLabel(string? source) => source switch
	{
		AiConfirmed => "Confirmed estimate",
		"AiRequested" => "AI estimate",
		"AiAutoClosed" => "Auto-estimated",
		UserAdjusted => "Adjusted",
		_ => "Estimate"
	};

	public static NutritionDayState StateOf(DailyNutritionSummaryResponse summary) =>
		summary.MealCount == 0 ? NutritionDayState.NoMeals
		: summary.AnalyzedMealCount == 0 ? NutritionDayState.NotAnalyzed
		: summary.AllAnalyzed ? NutritionDayState.Complete
		: NutritionDayState.Partial;

	// "No meals logged", "3 meals · not analyzed", "3 meals · 2 analyzed", "3 meals · all analyzed".
	// A partial total is never shown without this line.
	public static string CountLine(DailyNutritionSummaryResponse summary) => StateOf(summary) switch
	{
		NutritionDayState.NoMeals => "No meals logged",
		NutritionDayState.NotAnalyzed => $"{Meals(summary.MealCount)} · not analyzed",
		NutritionDayState.Complete => $"{Meals(summary.MealCount)} · all analyzed",
		_ => $"{Meals(summary.MealCount)} · {summary.AnalyzedMealCount} analyzed"
	};

	public static bool ShowsTotals(DailyNutritionSummaryResponse summary) => summary.AnalyzedMealCount > 0;

	// The day-level AI action, or null when there is nothing to analyze.
	public static string? AnalyzeLabel(DailyNutritionSummaryResponse summary, bool isToday) => StateOf(summary) switch
	{
		NutritionDayState.NotAnalyzed => isToday ? "Analyze today" : "Analyze day",
		NutritionDayState.Partial => "Analyze remaining",
		_ => null
	};

	// What a finished Analyze run needs to tell the user; null when everything went through.
	public static string? AnalysisMessage(NutritionAnalysisResponse analysis)
	{
		if (analysis.EstimationUnavailable)
		{
			return "Nutrition estimation is unavailable right now. Try again later.";
		}

		if (analysis.Failed > 0)
		{
			return analysis.Failed == 1
				? "1 meal could not be analyzed. Try again, or estimate it on its own."
				: $"{analysis.Failed} meals could not be analyzed. Try again, or estimate them one by one.";
		}

		return analysis.MorePending ? "More meals are waiting. Analyze remaining to continue." : null;
	}

	// The API trims the description; only a real text change clears nutrition.
	public static bool DescriptionChanged(string original, string edited) =>
		!string.Equals(original.Trim(), edited.Trim(), StringComparison.Ordinal);

	public static SetMealNutritionRequest Confirm(NutritionEstimateResponse proposal) =>
		new(proposal.CaloriesKcal, proposal.ProteinGrams, proposal.CarbsGrams, proposal.FatGrams, AiConfirmed);

	public static SetMealNutritionRequest Adjusted(decimal calories, decimal protein, decimal carbs, decimal fat) =>
		new(calories, protein, carbs, fat, UserAdjusted);

	private static string Meals(int count) => count == 1 ? "1 meal" : $"{count} meals";

	internal static string Whole(decimal value) => Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", Culture);
}

public enum NutritionDayState
{
	NoMeals,
	NotAnalyzed,
	Partial,
	Complete
}
