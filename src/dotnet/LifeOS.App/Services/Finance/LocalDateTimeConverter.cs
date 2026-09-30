namespace LifeOS.App.Services.Finance;

// Converts a wall-clock date and time entered by the user into an exact UTC instant.
// Plain .NET, no MAUI dependency.
public static class LocalDateTimeConverter
{
	// Fails with a readable message for a wall-clock time that does not exist (skipped by the
	// daylight-saving change) or is ambiguous (repeated by it).
	public static bool TryToUtc(DateTime localWallClock, TimeZoneInfo timeZone, out DateTimeOffset utc, out string? error)
	{
		var wallClock = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);

		if (timeZone.IsInvalidTime(wallClock))
		{
			utc = default;
			error = "Questo orario non esiste per il cambio dell'ora legale. Scegli un altro orario.";

			return false;
		}

		if (timeZone.IsAmbiguousTime(wallClock))
		{
			utc = default;
			error = "Questo orario è ambiguo per il cambio dell'ora legale. Scegli un altro orario.";

			return false;
		}

		utc = new DateTimeOffset(wallClock, timeZone.GetUtcOffset(wallClock)).ToUniversalTime();
		error = null;

		return true;
	}
}
