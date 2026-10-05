namespace LifeOS.App.Services.Navigation;

// The bottom dock (NAV-001): Home | Transactions | + | Nutrition | More. The high-frequency
// shortcuts, not the sitemap: More is the module directory and every module stays reachable there.
// "+" is the global quick add (Transaction, Meal), an action that never selects a section. Plain .NET.
//
// Active rules, on the normalized path (see Normalize); at most one item is active:
//   Home only on "/". Transactions on the history page and a transaction's detail/edit pages, but not
//   the quick-entry page, which belongs to "+". Nutrition on the Food diary (/nutrition and any nested
//   diary route), but not the Nutrition module's hub and Targets, which are module configuration.
//   More on the module directory, the Finance hub and its management screens, Gym, and the Nutrition
//   hub and Targets. Settings, Portfolio and the quick-entry page select none.
public static class DockNavigation
{
	public const string NewTransactionHref = "finance/transactions/new";

	// The quick-add Meal intent (?add=meal): the Food diary shows today, opens its add-meal form and
	// drops the query. It always means today, never the day the diary last showed.
	public const string AddQueryName = "add";
	public const string AddMealValue = "meal";
	public const string NewMealHref = "nutrition?" + AddQueryName + "=" + AddMealValue;

	private static readonly string[] NutritionModuleScreens = ["nutrition/hub", "nutrition/targets"];

	public static readonly IReadOnlyList<DockItem> Items =
	[
		new("Home", "home", "", path => path.Length == 0),
		new("Transactions", "transactions", "finance/transactions", IsTransactions),
		new("Quick add", "plus", null, _ => false, IsPrimary: true),
		new("Nutrition", "nutrition", "nutrition", IsFoodDiary),
		new("More", "grid", "more", IsMore)
	];

	// The "+" choices. Each opens an existing flow; the dock owns no creation logic.
	public static readonly IReadOnlyList<QuickAddAction> QuickAddActions =
	[
		new("Transaction", "Add transaction", "transactions", NewTransactionHref),
		new("Meal", "Add meal", "nutrition", NewMealHref)
	];

	// A base-relative path without query or fragment, slashes trimmed, lower case.
	public static string Normalize(string baseRelativePath)
	{
		var end = baseRelativePath.IndexOfAny(['?', '#']);

		return (end >= 0 ? baseRelativePath[..end] : baseRelativePath).Trim('/').ToLowerInvariant();
	}

	public static bool IsAddMeal(string? addQueryValue) =>
		string.Equals(addQueryValue, AddMealValue, StringComparison.OrdinalIgnoreCase);

	private static bool IsTransactions(string path) =>
		IsAt(path, "finance/transactions") && !IsAt(path, NewTransactionHref);

	private static bool IsFoodDiary(string path) =>
		IsAt(path, "nutrition") && !IsNutritionModuleScreen(path);

	private static bool IsMore(string path) =>
		IsAt(path, "more")
		|| (IsAt(path, "finance") && !IsAt(path, "finance/transactions"))
		|| IsAt(path, "gym")
		|| IsNutritionModuleScreen(path);

	private static bool IsNutritionModuleScreen(string path) =>
		NutritionModuleScreens.Any(screen => IsAt(path, screen));

	private static bool IsAt(string path, string section) =>
		path == section || path.StartsWith(section + "/", StringComparison.Ordinal);
}

// Href is null for the primary "+", which is an action (it opens the quick-add choices), not a link.
public sealed record DockItem(string Label, string Icon, string? Href, Func<string, bool> IsActive, bool IsPrimary = false);

public sealed record QuickAddAction(string Label, string AccessibleLabel, string Icon, string Href);
