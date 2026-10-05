namespace LifeOS.App.Services.Notifications;

// AUTO-001 §12: a random id per app installation, created once and kept in Preferences. It survives
// sign-outs and is lost on reinstall (Android backup is off), so a reinstall registers as a new
// installation. Never derived from hardware, advertising, user or token identifiers.
public static class InstallationId
{
	public const string PreferenceKey = "lifeos.installation-id";

	public static string GetOrCreate(Func<string?> load, Action<string> save)
	{
		var stored = load();

		if (IsValid(stored))
		{
			return stored!;
		}

		var created = Guid.NewGuid().ToString("D");
		save(created);

		return created;
	}

	// The API's format: 16–64 characters of letters, digits, '-' or '_'.
	public static bool IsValid(string? value) =>
		value is { Length: >= 16 and <= 64 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
