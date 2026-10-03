using System.Globalization;
using LifeOS.Contracts.Gym.History;

namespace LifeOS.App.Services.Gym;

// Workout history and previous-performance presentation: local dates, durations and recorded sets.
// Times are stored in UTC and shown in the device's time zone. Plain .NET.
public static class HistoryDisplay
{
	// The UI is English-only: textual dates use en-GB (day before month) whatever the device culture;
	// times follow the device culture.
	private static readonly CultureInfo DateTextCulture = CultureInfo.GetCultureInfo("en-GB");

	// Local day: "Today", "Yesterday", "28 Sep", "28 Sep 2025".
	public static string Day(DateTimeOffset utc, DateTime today, TimeZoneInfo timeZone)
	{
		var date = TimeZoneInfo.ConvertTime(utc, timeZone).Date;

		return date == today.Date ? "Today"
			: date == today.Date.AddDays(-1) ? "Yesterday"
			: date.Year == today.Year ? ShortDate(date)
			: $"{ShortDate(date)} {date.Year}";
	}

	// "28 Sep": the invariant three-letter month, stable across ICU versions (en-GB gives "Sept").
	private static string ShortDate(DateTime date) =>
		$"{date.Day} {CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(date.Month)}";

	// Local day and time for a history row: "Today 18:05", "28 Sep · 18:05".
	public static string When(DateTimeOffset utc, DateTime today, TimeZoneInfo timeZone, CultureInfo culture)
	{
		var day = Day(utc, today, timeZone);
		var time = Time(utc, timeZone, culture);

		return day is "Today" or "Yesterday" ? $"{day} {time}" : $"{day} · {time}";
	}

	// Full local date: "Tuesday 1 September 2026".
	public static string LongDate(DateTimeOffset utc, TimeZoneInfo timeZone) =>
		TimeZoneInfo.ConvertTime(utc, timeZone).ToString("dddd d MMMM yyyy", DateTextCulture);

	// Local time in the device culture: "18:05" (or "6:05 PM").
	public static string Time(DateTimeOffset utc, TimeZoneInfo timeZone, CultureInfo culture) =>
		TimeZoneInfo.ConvertTime(utc, timeZone).ToString(culture.DateTimeFormat.ShortTimePattern, culture);

	// "< 1 min", "52 min", "1 h 05 min".
	public static string Duration(DateTimeOffset startedAtUtc, DateTimeOffset completedAtUtc)
	{
		var minutes = (long)Math.Floor((completedAtUtc - startedAtUtc).TotalMinutes);

		return minutes < 1 ? "< 1 min"
			: minutes < 60 ? $"{minutes} min"
			: $"{minutes / 60} h {minutes % 60:00} min";
	}

	// Secondary line of a history row: "Today 18:05 · 52 min · 14 / 15 sets".
	public static string Summary(WorkoutHistoryItemResponse item, DateTime today, TimeZoneInfo timeZone, CultureInfo culture) =>
		$"{When(item.CompletedAtUtc, today, timeZone, culture)} · {Duration(item.StartedAtUtc, item.CompletedAtUtc)} · "
		+ WorkoutSessionDisplay.Progress(item.CompletedSetCount, item.PrescribedSetCount);

	// The last completed workout on Home: "Last workout · Push · Yesterday 18:05".
	public static string LastWorkout(WorkoutHistoryItemResponse item, DateTime today, TimeZoneInfo timeZone, CultureInfo culture) =>
		$"Last workout · {item.WorkoutName} · {When(item.CompletedAtUtc, today, timeZone, culture)}";

	// "82.5 kg × 8" or "BW × 12".
	public static string Set(PreviousSetResponse set) =>
		set.WeightKg is { } weight
			? $"{WorkoutSessionDisplay.FormatWeight(weight)} kg × {set.ActualReps}"
			: $"BW × {set.ActualReps}";

	// The recorded sets grouped by the block they were done in, in execution order: one group normally,
	// more when the exercise appeared in several blocks of the previous workout.
	public static IReadOnlyList<IReadOnlyList<PreviousSetResponse>> Occurrences(PreviousExercisePerformanceResponse previous) =>
		previous.Sets
			.OrderBy(set => set.BlockPosition)
			.ThenBy(set => set.Position)
			.GroupBy(set => set.BlockPosition)
			.Select(group => (IReadOnlyList<PreviousSetResponse>)group.ToList())
			.ToList();

	// The previous performance of each exercise, by ExerciseId.
	public static IReadOnlyDictionary<Guid, PreviousExercisePerformanceResponse> ByExercise(PreviousPerformanceResponse? previous) =>
		previous?.Exercises.ToDictionary(exercise => exercise.ExerciseId) ?? new Dictionary<Guid, PreviousExercisePerformanceResponse>();
}
