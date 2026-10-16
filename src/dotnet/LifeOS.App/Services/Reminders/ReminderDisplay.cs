using System.Globalization;
using LifeOS.Contracts.Reminders;

namespace LifeOS.App.Services.Reminders;

// REM-001 §4.1: how the app shows reminders. Plain .NET (unit-tested). The next occurrence, status and
// last outcome always come from the API; this only turns them into readable text. Dates use English
// names whatever the device culture, like Habits.
public static class ReminderDisplay
{
	public const string Title = "Reminders";

	public const string EmptyTitle = "Never forget the small things.";

	public const string EmptyHint = "Create a reminder for once, every day or some days of the week.";

	// Banners on the list (§4.1).
	public const string PushUnavailableBanner = "Push notifications aren't set up on the server yet, so reminders can't reach your phone.";

	public const string TimeZoneUnknownBanner = "LifeOS doesn't know your time zone yet, so reminders can't be scheduled. Open LifeOS again in a moment.";

	public const string NotificationsOffBanner = "Notifications are off for LifeOS on this device. Turn them on in your phone's settings to receive reminders.";

	public static readonly IReadOnlyList<DayOfWeek> Week =
		[DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

	// D-11: the snooze choices: minutes, button text, accessible name.
	public static readonly IReadOnlyList<(int Minutes, string Label, string AccessibleLabel)> SnoozeOptions =
		[(15, "15 min", "Snooze 15 minutes"), (60, "1 h", "Snooze 1 hour"), (180, "3 h", "Snooze 3 hours")];

	private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

	public static string WeekdayShort(DayOfWeek day) => day.ToString()[..2];

	// "Once on Tue 20 Oct", "Every day", "Mon, Thu".
	public static string RepeatText(ReminderScheduleResponse schedule, DateOnly today) => schedule.Kind switch
	{
		"Once" when schedule.Date is { } date => $"Once on {Day(date, today)}",
		"Daily" => "Every day",
		"Weekly" when schedule.Weekdays is { Count: 7 } => "Every day",
		"Weekly" => string.Join(", ", Week.Where(day => schedule.Weekdays?.Contains(day.ToString()) == true).Select(day => day.ToString()[..3])),
		_ => "Once"
	};

	// "08:30 · Every day".
	public static string ScheduleText(ReminderScheduleResponse schedule, DateOnly today) =>
		schedule.Kind == "Once" ? $"{RepeatText(schedule, today)} at {schedule.Time}" : $"{schedule.Time} · {RepeatText(schedule, today)}";

	// "Next: today 08:30", "Next: tomorrow 08:30", "Next: Tue 20 Oct 10:00"; null without one.
	public static string? NextText(ReminderResponse reminder, DateOnly today) =>
		TryParseLocal(reminder.NextDueLocal, out var next) ? $"Next: {When(next, today)}" : null;

	// "Last: sent today 08:30", "Last: muted yesterday 09:00", "Last: missed" (a skipped occurrence has no
	// time of its own); null before the first occurrence.
	public static string? LastText(ReminderResponse reminder, DateOnly today, TimeZoneInfo zone) =>
		reminder.LastOutcome switch
		{
			"Missed" => "Last: missed",
			{ } outcome when OutcomeText(outcome) is { } text && reminder.LastTriggeredAtUtc is { } at =>
				$"Last: {text} {When(TimeZoneInfo.ConvertTime(at, zone).DateTime, today)}",
			_ => null
		};

	// "Snoozed until 09:45" (local); null without a pending snooze.
	public static string? SnoozeText(ReminderResponse reminder, DateOnly today, TimeZoneInfo zone) =>
		reminder.SnoozeUntilUtc is { } until ? $"Snoozed until {When(TimeZoneInfo.ConvertTime(until, zone).DateTime, today)}" : null;

	public static string? OutcomeText(string? outcome) => outcome switch
	{
		"Notified" => "sent",
		"Missed" => "missed",
		"Disabled" => "muted",
		_ => null
	};

	public static string StatusText(string status) => status switch
	{
		"Paused" => "Paused",
		"Completed" => "Done",
		_ => "Active"
	};

	// "Take vitamins, every day at 08:30, next tomorrow 08:30": one announcement per list row.
	public static string RowLabel(ReminderResponse reminder, DateOnly today)
	{
		var schedule = reminder.Schedule switch
		{
			{ Kind: "Once", Date: { } date } => $"once, {Day(date, today)} at {reminder.Schedule.Time}",
			{ Kind: "Weekly", Weekdays.Count: < 7 } => $"{RepeatText(reminder.Schedule, today)} at {reminder.Schedule.Time}",
			_ => $"every day at {reminder.Schedule.Time}"
		};
		var next = TryParseLocal(reminder.NextDueLocal, out var at) ? $", next {When(at, today)}" : "";

		return $"{reminder.Label}, {schedule}{next}{(reminder.Status == "Paused" ? ", paused" : "")}";
	}

	// The editor's warning (D-9): the time is inside the user's quiet hours. Null otherwise.
	public static string? QuietHoursWarning(string? time, string? quietStart, string? quietEnd) =>
		NormalizeTime(time) is { } value && IsInsideQuietHours(value, quietStart, quietEnd)
			? $"{value} is inside your quiet hours ({NormalizeTime(quietStart)}–{NormalizeTime(quietEnd)}). It will be delivered when they end."
			: null;

	// Quiet hours are [start, end), crossing midnight when start > end (as on the server).
	public static bool IsInsideQuietHours(string? time, string? quietStart, string? quietEnd)
	{
		if (Parse(time) is not { } value || Parse(quietStart) is not { } start || Parse(quietEnd) is not { } end || start == end)
		{
			return false;
		}

		return start > end ? value >= start || value < end : value >= start && value < end;
	}

	// "HH:mm" or "HH:mm:ss" → "HH:mm"; anything else → null.
	public static string? NormalizeTime(string? value) => Parse(value)?.ToString("HH:mm", Culture);

	// "today 08:30", "tomorrow 08:30", "yesterday 08:30", "Tue 20 Oct 10:00" (with the year outside this one).
	public static string When(DateTime local, DateOnly today) => $"{Day(DateOnly.FromDateTime(local), today)} {local.ToString("HH:mm", Culture)}";

	public static string Day(DateOnly date, DateOnly today) =>
		date == today ? "today"
		: date == today.AddDays(1) ? "tomorrow"
		: date == today.AddDays(-1) ? "yesterday"
		: date.ToString(date.Year == today.Year ? "ddd d MMM" : "ddd d MMM yyyy", Culture);

	public static bool TryParseLocal(string? value, out DateTime local) =>
		DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm", Culture, DateTimeStyles.None, out local);

	private static TimeOnly? Parse(string? value) =>
		TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"], Culture, DateTimeStyles.None, out var time) ? time : null;
}
