using LifeOS.Domain.Finance.Categories;

namespace LifeOS.Application.Finance.Categories;

// A top-level starter category and the names of its subcategories, which share its type. Two levels
// by construction.
public sealed record StarterCategory(string Name, CategoryType CategoryType, IReadOnlyList<string> Children);

// The default category tree every new user receives during onboarding, as the user's own rows (fresh
// ids, freely editable later). Names and types only: no fixed ids, no seed data. A parent is itself
// selectable and is the generic fallback for its children, so there is no "Other" child.
public static class StarterCategories
{
    public static IReadOnlyList<StarterCategory> All { get; } =
    [
        new("Food & Drink", CategoryType.Expense, ["Groceries", "Eating out", "Bars & cafes"]),
        new("Transport", CategoryType.Expense, ["Fuel", "Car payment", "Insurance", "Maintenance"]),
        new("Home & Utilities", CategoryType.Expense, ["Housing", "Utilities", "Phone & internet"]),
        new("Health & Fitness", CategoryType.Expense, ["Medical", "Pharmacy", "Gym & sports"]),
        new("Shopping", CategoryType.Expense, ["Clothing", "Electronics", "Personal care"]),
        new("Leisure", CategoryType.Expense, ["Entertainment", "Events & concerts", "Hobbies"]),
        new("Travel", CategoryType.Expense, ["Accommodation", "Flights & long-distance transport"]),
        new("Personal & Gifts", CategoryType.Expense, ["Gifts", "Tobacco"]),
        new("Salary", CategoryType.Income, []),
        new("Bonus", CategoryType.Income, []),
        new("Interest & dividends", CategoryType.Income, []),
        new("Refunds", CategoryType.Income, []),
        new("Gifts", CategoryType.Income, []),
        new("Other income", CategoryType.Income, [])
    ];

    // New categories for the part of the starter tree this user does not have yet, with parents and
    // their children in one set (a child may reference a parent created in the same set).
    //
    // Names are compared ignoring case, as the sibling-name index does:
    //   - a starter parent is present when the user has a top-level category of the same type and name;
    //     an existing one is reused as the parent of the missing children;
    //   - a starter child is present only under that parent (user + type + parent + name). The same
    //     name elsewhere (top level, another parent) does not count.
    // Any other categories, custom ones or another user's, are left untouched and ignored.
    public static IReadOnlyList<Category> CreateMissing(
        Guid userId,
        IEnumerable<Category> existing,
        DateTimeOffset createdAtUtc)
    {
        var owned = existing.Where(category => category.UserId == userId).ToList();
        var missing = new List<Category>();

        foreach (var starter in All)
        {
            var parent = FindTopLevel(owned, starter);

            if (parent is null)
            {
                parent = Category.Create(userId, starter.Name, starter.CategoryType, parent: null, createdAtUtc);
                missing.Add(parent);
            }

            foreach (var childName in starter.Children)
            {
                if (!HasChild(owned, parent, childName))
                {
                    missing.Add(Category.Create(userId, childName, starter.CategoryType, parent, createdAtUtc));
                }
            }
        }

        return missing;
    }

    // Whether the user already has the complete starter tree (see CreateMissing).
    public static bool IsComplete(Guid userId, IEnumerable<Category> existing)
    {
        var owned = existing.Where(category => category.UserId == userId).ToList();

        return All.All(starter =>
            FindTopLevel(owned, starter) is { } parent
            && starter.Children.All(childName => HasChild(owned, parent, childName)));
    }

    private static Category? FindTopLevel(IEnumerable<Category> owned, StarterCategory starter) =>
        owned.FirstOrDefault(category =>
            category.ParentCategoryId is null
            && category.CategoryType == starter.CategoryType
            && SameName(category.Name, starter.Name));

    private static bool HasChild(IEnumerable<Category> owned, Category parent, string childName) =>
        owned.Any(category =>
            category.ParentCategoryId == parent.Id
            && category.CategoryType == parent.CategoryType
            && SameName(category.Name, childName));

    private static bool SameName(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
