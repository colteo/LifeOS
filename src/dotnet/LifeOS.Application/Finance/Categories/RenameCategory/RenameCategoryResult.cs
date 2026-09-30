using LifeOS.Application.Finance.Categories.GetCategories;

namespace LifeOS.Application.Finance.Categories.RenameCategory;

public enum RenameCategoryStatus
{
    Renamed,
    NotFound,
    DuplicateName
}

public sealed record RenameCategoryResult(RenameCategoryStatus Status, CategorySummary? Category)
{
    public static RenameCategoryResult Renamed(CategorySummary category) => new(RenameCategoryStatus.Renamed, category);

    public static RenameCategoryResult NotFound() => new(RenameCategoryStatus.NotFound, null);

    public static RenameCategoryResult DuplicateName() => new(RenameCategoryStatus.DuplicateName, null);
}
