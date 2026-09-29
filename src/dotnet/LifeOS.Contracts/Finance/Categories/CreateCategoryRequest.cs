namespace LifeOS.Contracts.Finance.Categories;

public sealed record CreateCategoryRequest(string Name, string Type, Guid? ParentCategoryId);
