using System.Globalization;
using LifeOS.App.Services.Finance;
using LifeOS.Contracts.Journal;

namespace LifeOS.App.Services.Journal;

// The values a journal request is built from: the instant in UTC, the title (null: no title) and the
// content, both trimmed as the API stores them.
public sealed record JournalEntryValues(DateTimeOffset OccurredAtUtc, string? Title, string Content);

// The journal entry being written or edited, as the form shows it, with the client-side checks that
// turn it into a request. Shared by New and Edit. The checks mirror JRN-001 (content required, title
// ≤ 200, content ≤ 20 000 after trimming) for usability only: the API remains authoritative. Text is
// never cut: too long is an error. Plain .NET, no MAUI.
public sealed class JournalEntryDraft
{
	public const int TitleMaxLength = 200;
	public const int ContentMaxLength = 20_000;

	public const string ContentRequiredMessage = "Write something in the entry.";
	public const string InvalidDateTimeMessage = "Choose a valid date and time.";

	// The HTML datetime-local value format: local wall-clock time without a time zone.
	private const string DateTimeLocalFormat = "yyyy-MM-ddTHH:mm";

	// Valid datetime-local values the WebView may return: minutes, seconds, or fractional seconds.
	private static readonly string[] AcceptedDateTimeLocalFormats =
	[
		"yyyy-MM-ddTHH:mm",
		"yyyy-MM-ddTHH:mm:ss",
		"yyyy-MM-ddTHH:mm:ss.FFFFFFF"
	];

	// Editing: while the date/time text is unchanged the stored instant is sent back exactly (seconds
	// and microseconds included), as the transaction editor does.
	private DateTimeOffset? _originalOccurredAtUtc;
	private string? _originalOccurredAtText;

	private JournalEntryDraft(string occurredAtLocalText)
	{
		OccurredAtLocalText = occurredAtLocalText;
	}

	// The single source of truth for the date/time field, bound as the raw string.
	public string OccurredAtLocalText { get; private set; }

	public string Title { get; set; } = string.Empty;

	public string Content { get; set; } = string.Empty;

	// Lengths as the API counts them (after trimming), for the counters.
	public int TitleLength => Title.Trim().Length;

	public int ContentLength => Content.Trim().Length;

	// A new entry happens now, in the device's local time (minute precision, like the picker).
	public static JournalEntryDraft ForNew(DateTime localNow) => new(FormatLocal(localNow));

	public static JournalEntryDraft From(JournalEntryResponse entry, TimeZoneInfo timeZone)
	{
		var text = FormatLocal(TimeZoneInfo.ConvertTime(entry.OccurredAtUtc, timeZone).DateTime);

		return new JournalEntryDraft(text)
		{
			Title = entry.Title ?? string.Empty,
			Content = entry.Content,
			_originalOccurredAtUtc = entry.OccurredAtUtc,
			_originalOccurredAtText = text
		};
	}

	// The picker's value, stored at minute precision when parsable; anything else as received
	// (building the request then reports it).
	public void SetOccurredAtFromPicker(string? raw)
	{
		raw = raw?.Trim() ?? string.Empty;

		OccurredAtLocalText = DateTime.TryParseExact(raw, AcceptedDateTimeLocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
			? FormatLocal(parsed)
			: raw;
	}

	public bool TryBuild(TimeZoneInfo timeZone, out JournalEntryValues values, out IReadOnlyList<string> errors)
	{
		values = null!;
		var problems = new List<string>();
		var occurredAtUtc = default(DateTimeOffset);

		if (_originalOccurredAtUtc is { } original && OccurredAtLocalText == _originalOccurredAtText)
		{
			occurredAtUtc = original;
		}
		else if (!DateTime.TryParseExact(OccurredAtLocalText, DateTimeLocalFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
		{
			problems.Add(InvalidDateTimeMessage);
		}
		else if (!LocalDateTimeConverter.TryToUtc(local, timeZone, out occurredAtUtc, out var timeError))
		{
			problems.Add(timeError!);
		}

		var title = Title.Trim();
		var content = Content.Trim();

		if (title.Length > TitleMaxLength)
		{
			problems.Add($"Title must be at most {TitleMaxLength} characters ({title.Length} now).");
		}

		if (content.Length == 0)
		{
			problems.Add(ContentRequiredMessage);
		}
		else if (content.Length > ContentMaxLength)
		{
			problems.Add($"Content must be at most {ContentMaxLength.ToString("N0", CultureInfo.InvariantCulture)} characters ({content.Length.ToString("N0", CultureInfo.InvariantCulture)} now).");
		}

		if (problems.Count == 0)
		{
			values = new JournalEntryValues(occurredAtUtc, title.Length == 0 ? null : title, content);
		}

		errors = problems;
		return problems.Count == 0;
	}

	public static CreateJournalEntryRequest ToCreateRequest(JournalEntryValues values) =>
		new(values.OccurredAtUtc, values.Title, values.Content);

	// Full replacement (JRN-001 PUT): a blank title is sent as null, which removes it.
	public static UpdateJournalEntryRequest ToUpdateRequest(JournalEntryValues values) =>
		new(values.OccurredAtUtc, values.Title, values.Content);

	private static string FormatLocal(DateTime localWallClock) =>
		localWallClock.ToString(DateTimeLocalFormat, CultureInfo.InvariantCulture);
}
