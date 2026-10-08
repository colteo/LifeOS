using System.Globalization;
using System.Text;
using LifeOS.App.Services.Finance;

namespace LifeOS.App.Services.Journal;

// Presentation of journal entries (JRN-002). OccurredAtUtc is shown in the device's local time with
// the existing LifeOS date/time wording (the transaction screens'). Plain .NET, no MAUI.
public static class JournalDisplay
{
	public const string Title = "Journal";

	public const int PreviewLength = 140;

	// List: "Today 18:05", "Yesterday 09:30", "27 September · 14:10", "27 September 2025 · 14:10".
	public static string ListDateTime(DateTimeOffset occurredAtUtc, DateTime today, TimeZoneInfo timeZone, CultureInfo culture) =>
		TransactionDisplay.CompactDateTime(occurredAtUtc, today, timeZone, culture);

	// Detail: "30 September 2026 · 13:10".
	public static string LongDateTime(DateTimeOffset occurredAtUtc, TimeZoneInfo timeZone, CultureInfo culture) =>
		TransactionDisplay.LongDateTime(occurredAtUtc, timeZone, culture);

	// A one-paragraph preview: whitespace and line breaks collapsed to single spaces, cut at a word
	// boundary when possible and marked with "…". The detail page always shows the full content.
	public static string Preview(string content, int maxLength = PreviewLength)
	{
		var builder = new StringBuilder(Math.Min(content.Length, maxLength + 1));
		var pendingSpace = false;

		foreach (var character in content)
		{
			if (char.IsWhiteSpace(character))
			{
				pendingSpace = builder.Length > 0;
				continue;
			}

			if (pendingSpace)
			{
				builder.Append(' ');
				pendingSpace = false;
			}

			builder.Append(character);

			if (builder.Length > maxLength)
			{
				break;
			}
		}

		if (builder.Length <= maxLength)
		{
			return builder.ToString();
		}

		var text = builder.ToString(0, maxLength);
		var lastSpace = text.LastIndexOf(' ');

		return (lastSpace > maxLength / 2 ? text[..lastSpace] : text).TrimEnd() + "…";
	}
}
