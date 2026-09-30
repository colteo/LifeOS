using LifeOS.Domain.Finance.Categories;

namespace LifeOS.Application.Finance.Categories;

public sealed record StarterCategory(string Name, CategoryType CategoryType);

// The default top-level categories every new user receives during onboarding, as the user's own
// rows (fresh ids, freely editable later). Names and types only: no fixed ids, no seed data.
public static class StarterCategories
{
    public static IReadOnlyList<StarterCategory> All { get; } =
    [
        new("Casa", CategoryType.Expense),
        new("Auto", CategoryType.Expense),
        new("Alimentari", CategoryType.Expense),
        new("Mangiare fuori", CategoryType.Expense),
        new("Salute", CategoryType.Expense),
        new("Shopping", CategoryType.Expense),
        new("Viaggi", CategoryType.Expense),
        new("Regali", CategoryType.Expense),
        new("Telefonia", CategoryType.Expense),
        new("Altro", CategoryType.Expense),
        new("Stipendio", CategoryType.Income),
        new("Bonus", CategoryType.Income),
        new("Rimborso", CategoryType.Income),
        new("Regalo", CategoryType.Income),
        new("Altro", CategoryType.Income)
    ];

    // Starter entries the user does not have yet. A starter entry is present when the user has a
    // top-level category of the same type with the same name, ignoring case. Any other categories
    // (custom ones, subcategories) neither count nor suppress the rest of the set.
    public static IReadOnlyList<StarterCategory> Missing(IEnumerable<Category> existing)
    {
        var present = existing
            .Where(category => category.ParentCategoryId is null)
            .Select(category => (category.CategoryType, category.Name))
            .ToList();

        return All
            .Where(starter => !present.Any(category =>
                category.CategoryType == starter.CategoryType
                && string.Equals(category.Name, starter.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
