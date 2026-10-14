using System.Globalization;
using LifeOS.Contracts.Habits;

namespace LifeOS.App.Services.Habits;

// HAB-001 §10: how the app shows habits. Plain .NET (unit-tested). Statuses, groups, streaks and
// adherence always come from the API; this only turns them into neutral, readable text (P-12: "Missed",
// never "Failed"), the date chips and the calendar grid. Dates use English names whatever the device
// culture, like the Food diary.
public static class HabitDisplay
{
	public const string Title = "Habits";

	public const string EmptyTitle = "Build small daily habits.";

	public const string EmptyHint = "Pick one small thing you want to do regularly. You can check it in with one tap.";

	// Today and the six days before it (HAB-001 P-5); older days through the habit's history.
	public const int ChipDays = 7;

	public const int CalendarWeeks = 5;

	public const int DefaultWindowDays = 30;

	public static readonly IReadOnlyList<int> WindowOptions = [7, 30, 90];

	public static readonly IReadOnlyList<DayOfWeek> Week =
		[DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

	public static readonly IReadOnlyList<(string Group, string Title)> Sections =
		[("ToDo", "To do"), ("Done", "Done"), ("NotToday", "Not today")];

	private static readonly CultureInfo DateCulture = CultureInfo.InvariantCulture;

	public static DateOnly Today(DateTime localNow) => DateOnly.FromDateTime(localNow);

	// ---- Dates ----

	// Today first, then the six days before it.
	public static IReadOnlyList<DateOnly> ChipDates(DateOnly today) =>
		Enumerable.Range(0, ChipDays).Select(offset => today.AddDays(-offset)).ToList();

	// "Today", "Yesterday", "Mon 12".
	public static string ChipLabel(DateOnly date, DateOnly today) =>
		date == today ? "Today" : date == today.AddDays(-1) ? "Yesterday" : date.ToString("ddd d", DateCulture);

	// "today", "yesterday", "Mon 12 Oct": inside a sentence.
	public static string DayPhrase(DateOnly date, DateOnly today) =>
		date == today ? "today" : date == today.AddDays(-1) ? "yesterday" : ShortDay(date);

	// "Mon 13 Oct".
	public static string ShortDay(DateOnly date) => date.ToString("ddd d MMM", DateCulture);

	// "14 Oct", or "14 Oct 2025" outside the current year.
	public static string ShortDate(DateOnly date, DateOnly today) =>
		date.ToString(date.Year == today.Year ? "d MMM" : "d MMM yyyy", DateCulture);

	public static string WeekdayShort(DayOfWeek day) => day.ToString()[..2];

	// ---- Frequency, streak, goals ----

	public static string FrequencyText(HabitFrequencyResponse frequency) => frequency.Kind switch
	{
		"Daily" => "Every day",
		"Weekdays" when frequency.Weekdays is { Count: 7 } => "Every day",
		"Weekdays" => string.Join(", ", Week
			.Where(day => frequency.Weekdays?.Contains(day.ToString()) == true)
			.Select(day => day.ToString()[..3])),
		"TimesPerWeek" when frequency.TimesPerWeek == 1 => "Once a week",
		"TimesPerWeek" => $"{frequency.TimesPerWeek} times a week",
		_ => "Archived"
	};

	// "Every day · since 14 Oct".
	public static string FrequencySince(HabitFrequencyResponse frequency, DateOnly today) =>
		$"{FrequencyText(frequency)} · since {ShortDate(frequency.EffectiveFrom, today)}";

	public static string StreakText(int streak, string unit) => unit == "Week"
		? streak == 1 ? "1 week" : $"{streak} weeks"
		: streak == 1 ? "1 day" : $"{streak} days";

	// "This week 2 / 3" (extras are shown: "4 / 3").
	public static string WeekText(HabitWeekResponse week) => $"This week {week.Done} / {week.Target}";

	// "21 of 26 days · 81%", or "Not enough data yet". Week-based counts are check-ins against targets.
	public static string AdherenceText(HabitAdherenceResponse adherence, string streakUnit) =>
		adherence.Percent is not { } percent
			? "Not enough data yet"
			: $"{adherence.Met} of {adherence.Expected} {(streakUnit == "Week" ? "check-ins" : adherence.Expected == 1 ? "day" : "days")} · {percent}%";

	public static string WindowLabel(int days) => $"{days} days";

	// The row's second line: the week goal for times-per-week habits, otherwise the frequency and streak.
	public static string RowDetail(HabitListItemResponse item) =>
		item.ThisWeek is { } week
			? $"{WeekText(week)} · {StreakText(item.CurrentStreak, item.StreakUnit)}"
			: $"{FrequencyText(item.Frequency)} · {StreakText(item.CurrentStreak, item.StreakUnit)}";

	// ---- Statuses ----

	public static string StatusLabel(string status) => status switch
	{
		"Done" => "Done",
		"Excused" => "Excused",
		"Missed" => "Missed",
		"Pending" => "To do",
		"NotScheduled" => "Not scheduled",
		_ => "Inactive"
	};

	// Shown next to the text, never instead of it (HAB-001 §10.1).
	public static string StatusSymbol(string status) => status switch
	{
		"Done" => "✓",
		"Excused" => "–",
		"Missed" => "✕",
		"Pending" => "○",
		"NotScheduled" => "·",
		_ => ""
	};

	public static bool IsChecked(string status) => status is "Done" or "Excused";

	public static string SectionTitle(string group, bool isToday) => group switch
	{
		"ToDo" => "To do",
		"Done" => "Done",
		_ => isToday ? "Not today" : "Not scheduled"
	};

	// The accessible name of the row's toggle.
	public static string ToggleLabel(string name, string status, DateOnly date, DateOnly today) =>
		IsChecked(status)
			? $"{name}, {StatusLabel(status).ToLowerInvariant()} {DayPhrase(date, today)}, open options"
			: $"Mark {name} done for {DayPhrase(date, today)}";

	// Read out after a change (aria-live).
	public static string Announcement(string name, HabitDayResponse day) => day.Status switch
	{
		"Done" => $"{name} done. Streak {StreakText(day.Progress.CurrentStreak, day.Progress.StreakUnit)}.",
		"Excused" => $"{name} excused.",
		_ => $"{name} is no longer checked in."
	};

	// Excused is meaningless on a day a day-based habit does not expect (the API rejects it).
	public static bool CanExcuse(HabitFrequencyResponse frequency, DateOnly date) => frequency.Kind switch
	{
		"Daily" or "TimesPerWeek" => true,
		"Weekdays" => frequency.Weekdays?.Contains(date.DayOfWeek.ToString()) == true,
		_ => false
	};

	// The row as it will look once the API confirms the change (shown at once, rolled back on error).
	public static HabitListItemResponse Optimistic(HabitListItemResponse item, string status, DateOnly date, DateOnly today)
	{
		var week = item.ThisWeek;

		if (week is not null && status == "Done" && item.Status != "Done")
		{
			week = week with { Done = week.Done + 1 };
		}
		else if (week is not null && item.Status == "Done" && status != "Done")
		{
			week = week with { Done = Math.Max(0, week.Done - 1) };
		}

		var cleared = !CanExcuse(item.Frequency, date) || (date != today && item.Frequency.Kind == "TimesPerWeek") ? "NotScheduled"
			: date == today ? "Pending"
			: "Missed";
		var newStatus = status is "Done" or "Excused" ? status : cleared;
		var group = IsChecked(newStatus) || week is { Target: > 0 } && week.Done >= week.Target ? "Done"
			: newStatus == "NotScheduled" && week is null ? "NotToday"
			: "ToDo";

		return item with { Status = newStatus, Group = group, ThisWeek = week, Note = status == "Excused" ? item.Note : null };
	}

	// "2 of 5 done" over the habits expected that day (Not today is left out).
	public static string HomeSummary(IReadOnlyList<HabitListItemResponse> habits)
	{
		var expected = habits.Count(item => item.Group != "NotToday");
		var done = habits.Count(item => item.Group == "Done");

		return expected == 0 ? "Nothing scheduled today" : $"{done} of {expected} done";
	}

	// ---- Calendar ----

	public sealed record CalendarCell(DateOnly Date, string? Status, string? Note, bool IsFuture, bool IsToday);

	public static DateOnly IsoWeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

	// Monday-Sunday rows from `from` (a Monday) for `weeks` weeks; days after today have no status.
	public static IReadOnlyList<IReadOnlyList<CalendarCell>> CalendarWeeksFrom(DateOnly from, int weeks, DateOnly today,
		IReadOnlyList<HabitDayItemResponse> days)
	{
		var byDate = days.ToDictionary(day => day.Date);

		return Enumerable.Range(0, weeks)
			.Select(week => (IReadOnlyList<CalendarCell>)Enumerable.Range(0, 7)
				.Select(offset =>
				{
					var date = from.AddDays(week * 7 + offset);
					var day = byDate.GetValueOrDefault(date);

					return new CalendarCell(date, day?.Status, day?.Note, date > today, date == today);
				})
				.ToList())
			.ToList();
	}

	// "Mon 13 Oct, Missed".
	public static string CellLabel(CalendarCell cell) =>
		cell.IsFuture ? $"{ShortDay(cell.Date)}, upcoming"
		: cell.Status is null ? ShortDay(cell.Date)
		: $"{ShortDay(cell.Date)}, {StatusLabel(cell.Status)}{(cell.Note is { Length: > 0 } note ? $": {note}" : "")}";

	// A day can be checked in when it is not in the future and the habit was active on it.
	public static bool CanChange(CalendarCell cell) => !cell.IsFuture && cell.Status is not null and not "Inactive";
}
