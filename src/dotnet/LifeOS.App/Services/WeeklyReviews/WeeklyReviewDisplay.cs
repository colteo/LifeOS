using System.Globalization;
using LifeOS.App.Services.Gym;

namespace LifeOS.App.Services.WeeklyReviews;

// Presentation of a saved weekly review (AUTO-002). The API's snapshot is authoritative for every
// value; these only decide wording. Deterministic: dates are the review's local dates (no device time
// zone involved), month names are invariant. Plain .NET, no MAUI.
public static class WeeklyReviewDisplay
{
	public const string Title = "Weekly Review";

	private static readonly DateTimeFormatInfo Months = CultureInfo.InvariantCulture.DateTimeFormat;

	// "1 – 7 Jun 2026", "29 Sep – 5 Oct 2026", "29 Dec 2025 – 4 Jan 2026".
	public static string WeekRange(DateOnly start, DateOnly end) =>
		start.Year != end.Year ? $"{ShortDate(start)} {start.Year} – {ShortDate(end)} {end.Year}"
		: start.Month != end.Month ? $"{ShortDate(start)} – {ShortDate(end)} {end.Year}"
		: $"{start.Day} – {ShortDate(end)} {end.Year}";

	// "Mon 29 Sep".
	public static string Day(DateOnly date) =>
		$"{Months.GetAbbreviatedDayName(date.DayOfWeek)} {ShortDate(date)}";

	// "< 1 min", "52 min", "1 h 05 min" (as workout history).
	public static string Duration(long seconds) =>
		HistoryDisplay.Duration(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(Math.Max(0, seconds)));

	public static string Workouts(int count) => count == 1 ? "1 workout" : $"{count} workouts";

	// "2 of 3 sets".
	public static string Sets(int completed, int prescribed) => $"{completed} of {prescribed} sets";

	// "3 of 7 days with meals".
	public static string DaysWithMeals(int days) => $"{days} of 7 days with meals";

	// Unanalyzed meals are stated, never estimated.
	public static string MealCoverage(int analyzed, int total) =>
		total == 0 ? "No meals logged"
		: analyzed == total ? (total == 1 ? "1 meal, analyzed" : $"All {total} meals analyzed")
		: $"{analyzed} of {total} meals analyzed";

	// Totals are shown only when at least one meal is analyzed, and are partial unless all are.
	public static bool ShowsNutritionTotals(int analyzed) => analyzed > 0;

	public static bool IsPartial(int analyzed, int total) => analyzed < total;

	private static string ShortDate(DateOnly date) => $"{date.Day} {Months.GetAbbreviatedMonthName(date.Month)}";
}
