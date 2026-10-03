namespace LifeOS.App.Services.Navigation;

// The dock's sections are LifeOS modules, not individual screens. A module's own pages select its
// item; the central New transaction is an action and never a selected section.
public enum DockSection
{
	None,
	Home,
	Finance,
	Gym,
	More
}

public static class DockNavigation
{
	public const string HomeHref = "";
	public const string FinanceHref = "finance";
	public const string NewTransactionHref = "finance/transactions/new";
	public const string GymHref = "gym";
	public const string MoreHref = "more";

	// The base-relative path without query, fragment or surrounding slashes, lower-case.
	public static string Normalize(string baseRelativePath)
	{
		var end = baseRelativePath.IndexOfAny(['?', '#']);

		return (end >= 0 ? baseRelativePath[..end] : baseRelativePath).Trim('/').ToLowerInvariant();
	}

	// Home only on "/"; Finance on its module and on Portfolio; Gym on its module; More on the module
	// directory (and later on modules without their own dock item). Settings and unknown pages select none.
	public static DockSection ActiveSection(string path) => path switch
	{
		"" => DockSection.Home,
		NewTransactionHref => DockSection.None,
		_ when IsAt(path, FinanceHref) || IsAt(path, "portfolio") => DockSection.Finance,
		_ when IsAt(path, GymHref) => DockSection.Gym,
		_ when IsAt(path, MoreHref) => DockSection.More,
		_ => DockSection.None
	};

	private static bool IsAt(string path, string section) =>
		path == section || path.StartsWith(section + "/", StringComparison.Ordinal);
}
