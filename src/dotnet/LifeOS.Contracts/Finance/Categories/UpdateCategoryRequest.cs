namespace LifeOS.Contracts.Finance.Categories;

// Only the name of a category is editable: its type and parent cannot be changed.
public sealed record UpdateCategoryRequest(string Name);
