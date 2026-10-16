using System.Globalization;
using LifeOS.Contracts.Reminders;

namespace LifeOS.App.Services.Reminders;

// The reminder editor's values (REM-001 §4.1): what, optional note, repeat (once / every day / some
// days), the date (once) or weekdays (some days) and the time. A conditional reminder (R5: a habit or the
// water target) has a fixed label instead of a title and note, and repeats every day or on some days.
// Plain .NET. Validate gives quick feedback
// with the API's limits and messages; the API still validates everything and computes the next
// occurrence. Text is never cut (too long is an error).
public sealed class ReminderDraft
{
	public const int TitleMaxLength = 80;
	public const int NoteMaxLength = 500;

	public const string Once = "Once";
	public const string Daily = "Daily";
	public const string Weekly = "Weekly";

	public static readonly IReadOnlyList<(string Kind, string Label)> Repeats =
		[(Once, "Once"), (Daily, "Every day"), (Weekly, "Some days")];

	public const string Custom = "Custom";
	public const string HabitIncomplete = "HabitIncomplete";
	public const string HydrationGoal = "HydrationGoal";

	public const string HydrationLabel = "If today's water target isn't reached";

	private readonly HashSet<DayOfWeek> _weekdays = [];

	public string Title { get; set; } = "";

	public string? Note { get; set; }

	public string Repeat { get; set; } = Once;

	public DateOnly Date { get; set; }

	// "HH:mm" from <input type="time"> (a browser may add seconds; they are dropped).
	public string Time { get; set; } = "09:00";

	public bool IsEditing { get; private init; }

	// "Custom", "HabitIncomplete" or "HydrationGoal"; fixed once created.
	public string Kind { get; private init; } = Custom;

	public bool IsConditional => Kind != Custom;

	// HabitIncomplete only.
	public Guid? HabitId { get; private init; }

	// What a conditional reminder is about ("If “Read” isn't done"); shown instead of the title.
	public string Label { get; set; } = "";

	// A conditional reminder repeats: no "Once".
	public IReadOnlyList<(string Kind, string Label)> RepeatOptions => IsConditional ? Repeats.Where(repeat => repeat.Kind != Once).ToList() : Repeats;

	public IReadOnlyCollection<DayOfWeek> SelectedWeekdays => _weekdays;

	public int TitleLength => Title.Trim().Length;

	public int NoteLength => (Note ?? "").Trim().Length;

	// yyyy-MM-dd for <input type="date">.
	public string DateText => Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	// A new reminder: once, at the next full hour (today, or tomorrow after 23:00).
	public static ReminderDraft ForNew(DateTime localNow)
	{
		var next = localNow.Date.AddHours(localNow.Hour + 1);
		var draft = new ReminderDraft
		{
			Date = DateOnly.FromDateTime(next),
			Time = next.ToString("HH:mm", CultureInfo.InvariantCulture)
		};

		draft._weekdays.Add(next.DayOfWeek);
		return draft;
	}

	// A new habit (20:00, HAB-US12) or water (17:00, REM-US9) nudge, every day.
	public static ReminderDraft ForConditional(string kind, Guid? habitId, string label, DateTime localNow)
	{
		var draft = new ReminderDraft
		{
			Kind = kind == HabitIncomplete ? HabitIncomplete : HydrationGoal,
			HabitId = kind == HabitIncomplete ? habitId : null,
			Label = label,
			Repeat = Daily,
			Date = DateOnly.FromDateTime(localNow),
			Time = kind == HabitIncomplete ? "20:00" : "17:00"
		};

		draft._weekdays.Add(localNow.DayOfWeek);
		return draft;
	}

	// "If “Read 20 pages” isn't done".
	public static string HabitLabel(string? habitName) => habitName is null ? "If a habit isn't done" : $"If “{habitName}” isn't done";

	public static ReminderDraft From(ReminderResponse reminder, DateOnly today)
	{
		var conditional = reminder.Kind is HabitIncomplete or HydrationGoal;
		var draft = new ReminderDraft
		{
			IsEditing = true,
			Kind = conditional ? reminder.Kind : Custom,
			HabitId = reminder.HabitId,
			Label = reminder.Label,
			Title = reminder.Title ?? "",
			Note = reminder.Note,
			Repeat = reminder.Schedule.Kind is Once or Daily or Weekly ? reminder.Schedule.Kind : Once,
			Date = reminder.Schedule.Date ?? today,
			Time = reminder.Schedule.Time
		};

		foreach (var name in reminder.Schedule.Weekdays ?? [])
		{
			if (Enum.TryParse<DayOfWeek>(name, out var day))
			{
				draft._weekdays.Add(day);
			}
		}

		return draft;
	}

	public bool IsSelected(DayOfWeek day) => _weekdays.Contains(day);

	public void ToggleWeekday(DayOfWeek day)
	{
		if (!_weekdays.Remove(day))
		{
			_weekdays.Add(day);
		}
	}

	public void SetDateFromPicker(string? value)
	{
		if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
		{
			Date = date;
		}
	}

	// "HH:mm" or "HH:mm:ss" → "HH:mm"; anything else → null.
	public string? NormalizedTime => ReminderDisplay.NormalizeTime(Time);

	public IReadOnlyList<string> Validate(DateTime localNow)
	{
		var errors = new List<string>();

		if (IsConditional)
		{
			if (Repeat == Once)
			{
				errors.Add("Choose every day or some days.");
			}
		}
		else if (TitleLength == 0)
		{
			errors.Add("Say what to remind you about.");
		}
		else if (TitleLength > TitleMaxLength)
		{
			errors.Add($"The title must be at most {TitleMaxLength} characters.");
		}

		if (!IsConditional && NoteLength > NoteMaxLength)
		{
			errors.Add($"The note must be at most {NoteMaxLength} characters.");
		}

		if (NormalizedTime is not { } time)
		{
			errors.Add("Choose a time.");
		}
		else if (Repeat == Once && Date.ToDateTime(TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture)) <= localNow)
		{
			errors.Add("Choose a date and time in the future.");
		}

		if (Repeat == Weekly && _weekdays.Count == 0)
		{
			errors.Add("Choose at least one day.");
		}

		return errors;
	}

	public ReminderScheduleRequest Schedule() => Repeat switch
	{
		Daily => new ReminderScheduleRequest(Daily, null, null, NormalizedTime),
		Weekly => new ReminderScheduleRequest(Weekly, null, ReminderDisplay.Week.Where(_weekdays.Contains).Select(day => day.ToString()).ToList(),
			NormalizedTime),
		_ => new ReminderScheduleRequest(Once, Date, null, NormalizedTime)
	};

	public CreateReminderRequest ToCreateRequest(int utcOffsetMinutes) =>
		IsConditional
			? new(Kind, null, null, Schedule(), utcOffsetMinutes, HabitId)
			: new(Custom, Title.Trim(), BlankToNull(Note), Schedule(), utcOffsetMinutes);

	public UpdateReminderRequest ToUpdateRequest(int utcOffsetMinutes) =>
		IsConditional
			? new(null, null, Schedule(), utcOffsetMinutes)
			: new(Title.Trim(), BlankToNull(Note), Schedule(), utcOffsetMinutes);

	private static string? BlankToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
