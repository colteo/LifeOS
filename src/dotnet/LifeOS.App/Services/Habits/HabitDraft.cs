using System.Globalization;
using LifeOS.Contracts.Habits;

namespace LifeOS.App.Services.Habits;

// The habit editor's values (HAB-001 §10): name, optional description, how often and, when creating,
// the type (manual or "Reach my water target", HAB-US11) and the start date. Plain .NET. Validate gives quick feedback with the API's limits and messages; the API
// still validates everything. Text is never cut (too long is an error).
public sealed class HabitDraft
{
	public const int NameMaxLength = 80;
	public const int DescriptionMaxLength = 500;
	public const int NoteMaxLength = 280;

	public const string Daily = "Daily";
	public const string Weekdays = "Weekdays";
	public const string TimesPerWeek = "TimesPerWeek";

	public static readonly IReadOnlyList<(string Kind, string Label)> Kinds =
		[(Daily, "Every day"), (Weekdays, "On specific days"), (TimesPerWeek, "Times per week")];

	public const string Manual = "Manual";
	public const string Hydration = "Hydration";

	public const string WaterHabitName = "Reach my water target";

	public static readonly IReadOnlyList<(string Source, string Label)> Sources =
		[(Manual, "I check it in"), (Hydration, "Reach my water target")];

	private readonly HashSet<DayOfWeek> _weekdays = [];
	private HabitFrequencyRequest? _original;

	public string Name { get; set; } = "";

	public string? Description { get; set; }

	public string Kind { get; set; } = Daily;

	public int TimesPerWeekValue { get; set; } = 3;

	public DateOnly StartsOn { get; set; }

	// "Manual" or "Hydration"; chosen when creating only.
	public string CompletionSource { get; private set; } = Manual;

	public bool IsWater => CompletionSource == Hydration;

	// False while the habit is archived: its frequency can change only after Restore (409 otherwise).
	public bool FrequencyEditable { get; private init; } = true;

	public bool IsEditing => _original is not null;

	public IReadOnlyCollection<DayOfWeek> SelectedWeekdays => _weekdays;

	public int NameLength => Name.Trim().Length;

	public int DescriptionLength => (Description ?? "").Trim().Length;

	// yyyy-MM-dd for <input type="date">.
	public string StartsOnText => StartsOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	public static HabitDraft ForNew(DateOnly today)
	{
		var draft = new HabitDraft { StartsOn = today };

		foreach (var day in new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
		{
			draft._weekdays.Add(day);
		}

		return draft;
	}

	// An archived habit shows its last frequency, read-only.
	public static HabitDraft From(HabitDetailResponse habit)
	{
		var archived = habit.Status == "Archived";
		var frequency = archived ? habit.LastActiveFrequency ?? habit.Frequency : habit.Frequency;
		var draft = new HabitDraft
		{
			Name = habit.Name,
			Description = habit.Description,
			StartsOn = habit.StartsOn,
			FrequencyEditable = !archived,
			CompletionSource = habit.CompletionSource == Hydration ? Hydration : Manual,
			Kind = frequency.Kind is Daily or Weekdays or TimesPerWeek ? frequency.Kind : Daily,
			TimesPerWeekValue = frequency.TimesPerWeek ?? 3
		};

		foreach (var name in frequency.Weekdays ?? [])
		{
			if (Enum.TryParse<DayOfWeek>(name, out var day))
			{
				draft._weekdays.Add(day);
			}
		}

		draft._original = draft.Frequency();
		return draft;
	}

	public bool IsSelected(DayOfWeek day) => _weekdays.Contains(day);

	// The water template fills an empty name; switching back clears it only if it was not edited.
	public void SetCompletionSource(string source)
	{
		if (IsEditing || source == CompletionSource || source is not (Manual or Hydration))
		{
			return;
		}

		if (source == Hydration && Name.Trim().Length == 0)
		{
			Name = WaterHabitName;
		}
		else if (source == Manual && Name == WaterHabitName)
		{
			Name = "";
		}

		CompletionSource = source;
	}

	public void ToggleWeekday(DayOfWeek day)
	{
		if (!_weekdays.Remove(day))
		{
			_weekdays.Add(day);
		}
	}

	public void SetStartsOnFromPicker(string? value)
	{
		if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
		{
			StartsOn = date;
		}
	}

	public HabitFrequencyRequest Frequency() => Kind switch
	{
		Weekdays => new HabitFrequencyRequest(Weekdays,
			HabitDisplay.Week.Where(_weekdays.Contains).Select(day => day.ToString()).ToList(), null),
		TimesPerWeek => new HabitFrequencyRequest(TimesPerWeek, null, TimesPerWeekValue),
		_ => new HabitFrequencyRequest(Daily, null, null)
	};

	// Editing only: whether Save changes the frequency (from today).
	public bool FrequencyChanged => _original is { } original && !Same(original, Frequency());

	public IReadOnlyList<string> Validate()
	{
		var errors = new List<string>();

		if (NameLength == 0)
		{
			errors.Add("Give the habit a name.");
		}
		else if (NameLength > NameMaxLength)
		{
			errors.Add($"The name must be at most {NameMaxLength} characters.");
		}

		if (DescriptionLength > DescriptionMaxLength)
		{
			errors.Add($"The description must be at most {DescriptionMaxLength} characters.");
		}

		if (FrequencyEditable && Kind == Weekdays && _weekdays.Count == 0)
		{
			errors.Add("Choose at least one day of the week.");
		}

		if (FrequencyEditable && Kind == TimesPerWeek && TimesPerWeekValue is < 1 or > 7)
		{
			errors.Add("Choose between 1 and 7 times per week.");
		}

		return errors;
	}

	public CreateHabitRequest ToCreateRequest(int utcOffsetMinutes) =>
		new(Name.Trim(), BlankToNull(Description), CompletionSource, Frequency(), StartsOn, utcOffsetMinutes);

	// The frequency is sent only when it changed (and can change), so a rename never adds a version.
	public UpdateHabitRequest ToUpdateRequest(int utcOffsetMinutes) =>
		new(Name.Trim(), BlankToNull(Description), FrequencyEditable && FrequencyChanged ? Frequency() : null, utcOffsetMinutes);

	private static string? BlankToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

	private static bool Same(HabitFrequencyRequest first, HabitFrequencyRequest second) =>
		first.Kind == second.Kind
		&& first.TimesPerWeek == second.TimesPerWeek
		&& (first.Weekdays ?? []).SequenceEqual(second.Weekdays ?? []);
}
