using System.Globalization;

namespace LifeOS.App.Services;

// Formats an instant for a query-string parameter the API requires to be explicit UTC
// (e.g. fromUtc, toUtc, atUtc): "yyyy-MM-ddTHH:mm:ss.fffffffZ", URI-escaped.
internal static class UtcQueryValue
{
	public static string Format(DateTimeOffset value) =>
		Uri.EscapeDataString(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
}
