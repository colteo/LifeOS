using System.Globalization;
using LifeOS.Contracts.Nutrition.Hydration;

namespace LifeOS.App.Services.Nutrition;

// Presentation rules of Hydration (HYD-001 §4). Plain .NET, no MAUI. The API is authoritative for
// validation, totals and whether a target is met; these only shape what the pages show and send.
// Copy stays neutral: over the target is fine, nothing judges or advises (P-9).
public static class HydrationFormat
{
	public const int MinAmountMl = 1;
	public const int MaxAmountMl = 4000;
	public const int MinTargetMl = 250;
	public const int MaxTargetMl = 10000;

	// The Undo snackbar stays at least this long (and while it has focus).
	public static readonly TimeSpan UndoDuration = TimeSpan.FromSeconds(6);

	// "Last 7 days": today and the six days before it.
	public const int HistoryDays = 7;

	private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

	// "250 ml" below 1000; "1 l", "1.25 l" from 1000 (P-3).
	public static string Amount(int ml) =>
		ml < 1000 ? $"{ml.ToString(Invariant)} ml" : $"{(ml / 1000m).ToString("0.##", Invariant)} l";

	// "1250 / 2000 ml" with a target (both in ml, so they compare at a glance); "1.25 l today" or
	// "1.25 l" without one.
	public static string TotalLine(HydrationDaySummaryResponse day, bool isToday) =>
		day.TargetMl is { } target
			? $"{day.TotalMl.ToString(Invariant)} / {target.ToString(Invariant)} ml"
			: isToday ? $"{Amount(day.TotalMl)} today" : Amount(day.TotalMl);

	// "63%"; may exceed 100. Null without a target.
	public static string? PercentText(HydrationDaySummaryResponse day) =>
		day.ProgressPercent is { } percent ? $"{percent.ToString(Invariant)}%" : null;

	// The bar's fill, capped at 100: an over-target day is simply full.
	public static int BarPercent(HydrationDaySummaryResponse day) => Math.Clamp(day.ProgressPercent ?? 0, 0, 100);

	// The text equivalent read by screen readers (aria-live), in full words.
	public static string Announcement(HydrationDaySummaryResponse day, bool isToday)
	{
		var when = isToday ? "today" : "on this day";

		return day.TargetMl is { } target
			? $"Water {when} {day.TotalMl.ToString(Invariant)} of {target.ToString(Invariant)} millilitres"
			: $"Water {when} {day.TotalMl.ToString(Invariant)} millilitres";
	}

	public static string QuickAddLabel(int ml) => $"+ {Amount(ml)}";

	public static string QuickAddAccessibleName(int ml) => $"Add {ml.ToString(Invariant)} millilitres of water";

	public static string AddedMessage(int ml) => $"Added {Amount(ml)}";

	public static string EmptyMessage(DateOnly day, DateOnly today) =>
		day == today ? "No water logged today." : "No water logged on this day.";

	// "08:10 · 250 ml"
	public static string IntakeLine(HydrationIntakeResponse intake) => $"{MealJournal.TimeText(intake.Time)} · {Amount(intake.AmountMl)}";

	// One "Last 7 days" row: "Mon 13 · 1800 / 2000 ml" or "Sat 11 · 1.5 l".
	public static string HistoryRow(HydrationDaySummaryResponse day) =>
		$"{day.Date.ToString("ddd d", Invariant)} · {TotalLine(day, isToday: false)}";

	// "Met 4 of 6 days with a target · avg 1720 ml"; without targeted days only the average.
	public static string HistorySummary(HydrationRangeSummaryResponse summary)
	{
		var average = $"avg {summary.AverageTotalMl.ToString(Invariant)} ml";

		return summary.DaysWithTarget == 0
			? average
			: $"Met {summary.DaysMet.ToString(Invariant)} of {summary.DaysWithTarget.ToString(Invariant)} days with a target · {average}";
	}

	public static (DateOnly From, DateOnly To) HistoryRange(DateOnly today) => (today.AddDays(-(HistoryDays - 1)), today);

	// P-11: the Home card is for users who use Hydration: a current target or water logged today.
	public static bool ShowsHomeCard(HydrationSettingsResponse settings, HydrationDaySummaryResponse today) =>
		settings.TargetMl is not null || today.TotalMl > 0;

	// "Applies from today. Past days keep their target." plus, when a target exists, since when.
	public static string TargetNote(HydrationSettingsResponse settings, DateOnly today) =>
		settings.TargetEffectiveFrom is { } from && from < today
			? $"{TargetState(settings)} since {from.ToString("d MMM yyyy", Invariant)}. Changes apply from today; past days keep their target."
			: "Changes apply from today; past days keep their target.";

	private static string TargetState(HydrationSettingsResponse settings) =>
		settings.TargetMl is { } target ? $"{target.ToString(Invariant)} ml" : "No daily target";

	public static AddHydrationIntakeRequest AddRequest(int amountMl, DateOnly day, TimeOnly time, TimeZoneInfo timeZone) =>
		new(amountMl, day, time, MealJournal.UtcOffsetMinutes(day, time, timeZone));

	// The device's current offset, for "today"-based writes (target, quick-add).
	public static int CurrentUtcOffsetMinutes(DateTimeOffset localNow) => (int)localNow.Offset.TotalMinutes;

	// Convenience checks only; the API validates everything.
	public static string? AmountError(int? ml) =>
		ml is null or < MinAmountMl or > MaxAmountMl ? $"Enter an amount between {MinAmountMl} and {MaxAmountMl} ml." : null;

	// Empty is "no daily target".
	public static string? TargetError(int? ml) =>
		ml is < MinTargetMl or > MaxTargetMl ? $"Enter a daily target between {MinTargetMl} and {MaxTargetMl} ml, or leave it empty." : null;

	public static IReadOnlyList<string> QuickAddErrors(IReadOnlyList<int?> amounts) =>
		amounts.Count != 3 || amounts.Any(amount => AmountError(amount) is not null)
			? [$"Enter three quick-add amounts between {MinAmountMl} and {MaxAmountMl} ml."]
			: [];
}
