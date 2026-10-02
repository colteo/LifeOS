namespace LifeOS.Application.Finance.Categories.DeleteCategory;

public enum DeleteCategoryResult
{
    Deleted,

    // Missing, or another user's category.
    NotFound,

    // The category has subcategories; they are never deleted with it.
    HasSubcategories,

    // Transactions reference the category.
    InUse,
    HasRecurringRules
}
