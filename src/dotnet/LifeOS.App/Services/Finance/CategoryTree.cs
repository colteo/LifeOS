using LifeOS.Contracts.Finance.Categories;

namespace LifeOS.App.Services.Finance;

public sealed record CategoryNode(CategoryResponse Category, IReadOnlyList<CategoryResponse> Children);

// Unlinked: categories of the section whose parent is not one of its top-level categories.
// The API's rules make this empty; it exists so the page never loses or crashes on such rows.
public sealed record CategorySection(IReadOnlyList<CategoryNode> TopLevel, IReadOnlyList<CategoryResponse> Unlinked);

// Builds the two-level category tree of one type (Income or Expense) from the API's flat list.
// Grouping uses only Id and ParentCategoryId, never names. The API's order is not relied on:
// each level is sorted by name (case-insensitive, as the API compares names), then by Id.
// Plain .NET, no MAUI.
public static class CategoryTree
{
	public static CategorySection Build(IEnumerable<CategoryResponse> categories, string type)
	{
		var ofType = categories.Where(category => category.Type == type).ToList();

		var topLevel = ofType
			.Where(category => category.ParentCategoryId is null)
			.ToList();

		var topLevelIds = topLevel.Select(category => category.Id).ToHashSet();

		var childrenByParent = ofType
			.Where(category => category.ParentCategoryId is { } parentId && topLevelIds.Contains(parentId))
			.ToLookup(category => category.ParentCategoryId!.Value);

		var unlinked = ofType
			.Where(category => category.ParentCategoryId is { } parentId && !topLevelIds.Contains(parentId));

		return new CategorySection(
			Sorted(topLevel)
				.Select(parent => new CategoryNode(parent, Sorted(childrenByParent[parent.Id]).ToList()))
				.ToList(),
			Sorted(unlinked).ToList());
	}

	private static IEnumerable<CategoryResponse> Sorted(IEnumerable<CategoryResponse> categories) =>
		categories
			.OrderBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(category => category.Id);
}
