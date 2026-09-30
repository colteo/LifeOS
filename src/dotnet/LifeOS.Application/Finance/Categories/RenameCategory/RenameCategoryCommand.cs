namespace LifeOS.Application.Finance.Categories.RenameCategory;

// Only the name is editable: the type and the parent of a category never change.
public sealed record RenameCategoryCommand(Guid CategoryId, string Name);
