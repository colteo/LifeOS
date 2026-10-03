namespace LifeOS.App.Services.Navigation;

// The bottom dock (APP-001): Transactions | + | More. The high-frequency shortcuts, not the sitemap:
// Home is the header wordmark and everything else is reached through More. Plain .NET.
//
// Active rules, on the normalized path (see Normalize):
//   Transactions on the history page and a transaction's detail/edit pages, but not the quick-entry
//   page, which belongs to "+"; "+" is an action, never a selected section; More on the module
//   directory, the Finance module's management screens and Nutrition. Home and Settings select none.
public static class DockNavigation
{
	public const string NewTransactionHref = "finance/transactions/new";

	public static readonly IReadOnlyList<DockItem> Items =
	[
		new("Transactions", "transactions", "finance/transactions", IsTransactions),
		new("New transaction", "plus", NewTransactionHref, _ => false, IsPrimary: true),
		new("More", "grid", "more", path =>
			IsAt(path, "more")
			|| path == "finance"
			|| IsAt(path, "finance/accounts")
			|| IsAt(path, "finance/categories")
			|| IsAt(path, "nutrition"))
	];

	// A base-relative path without query or fragment, slashes trimmed, lower case.
	public static string Normalize(string baseRelativePath)
	{
		var end = baseRelativePath.IndexOfAny(['?', '#']);

		return (end >= 0 ? baseRelativePath[..end] : baseRelativePath).Trim('/').ToLowerInvariant();
	}

	private static bool IsTransactions(string path) =>
		IsAt(path, "finance/transactions") && !IsAt(path, NewTransactionHref);

	private static bool IsAt(string path, string section) =>
		path == section || path.StartsWith(section + "/", StringComparison.Ordinal);
}

public sealed record DockItem(string Label, string Icon, string Href, Func<string, bool> IsActive, bool IsPrimary = false);
