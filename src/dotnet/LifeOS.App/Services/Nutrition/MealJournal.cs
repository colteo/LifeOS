using System.Globalization;
using LifeOS.Contracts.Nutrition;

namespace LifeOS.App.Services.Nutrition;

// Presentation rules of the meal journal (NUT-001). Plain .NET, no MAUI. The API is authoritative
// for validation and order; these only shape what the page shows and sends.
public static class MealJournal
{
	// The selectable meal types; the form's first option is "Optional" (none).
	public static readonly IReadOnlyList<string> MealTypes = ["Breakfast", "Lunch", "Dinner", "Snack", "Other"];

	// The suggested time when adding on a day other than today.
	public static readonly TimeOnly OtherDayDefaultTime = new(12, 0);

	private static readonly CultureInfo DateCulture = CultureInfo.InvariantCulture;

	public static DateOnly Today(DateTime localNow) => DateOnly.FromDateTime(localNow);

	// "Today", otherwise "2 October 2026" (English month names, whatever the device culture).
	public static string DayLabel(DateOnly day, DateOnly today) =>
		day == today ? "Today" : day.ToString("d MMMM yyyy", DateCulture);

	public static string EmptyMessage(DateOnly day, DateOnly today) =>
		day == today ? "No meals logged today." : "No meals logged on this day.";

	// The journal is a diary of what was eaten: it stops at today.
	public static bool CanGoForward(DateOnly day, DateOnly today) => day < today;

	// "13:10 · Lunch", or "13:10" without a meal type.
	public static string Heading(MealResponse meal) =>
		meal.MealType is { Length: > 0 } type ? $"{TimeText(meal.Time)} · {type}" : TimeText(meal.Time);

	public static string TimeText(TimeOnly time) => time.ToString("HH:mm", DateCulture);

	// The time an Add form starts with: now (to the minute) on today, midday on another day.
	public static TimeOnly DefaultTime(DateOnly day, DateTime localNow)
	{
		if (day != Today(localNow))
		{
			return OtherDayDefaultTime;
		}

		var now = TimeOnly.FromDateTime(localNow);

		return new TimeOnly(now.Hour, now.Minute);
	}

	// The device's offset from UTC, in minutes, for that wall-clock date and time.
	public static int UtcOffsetMinutes(DateOnly day, TimeOnly time, TimeZoneInfo timeZone) =>
		(int)timeZone.GetUtcOffset(day.ToDateTime(time, DateTimeKind.Unspecified)).TotalMinutes;

	// Most recent first: the same order the API returns, kept if the list is ever re-sorted locally.
	public static IReadOnlyList<MealResponse> NewestFirst(IEnumerable<MealResponse> meals) =>
		meals.OrderByDescending(meal => meal.Time).ThenByDescending(meal => meal.Id).ToList();

	// The form's meal type value: "" is none.
	public static string? MealTypeOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

	public static CreateMealRequest CreateRequest(string description, string? mealType, DateOnly day, TimeOnly time, TimeZoneInfo timeZone) =>
		new(description, MealTypeOrNull(mealType), day, time, UtcOffsetMinutes(day, time, timeZone));

	// clearNutrition: the user confirmed that a new description clears the meal's nutrition (NUT-002).
	public static UpdateMealRequest UpdateRequest(string description, string? mealType, TimeOnly time, bool clearNutrition = false) =>
		new(description, MealTypeOrNull(mealType), time, clearNutrition ? true : null);
}
