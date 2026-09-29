using LifeOS.Domain.Finance.Categories;

namespace LifeOS.Application.Finance.Categories.CreateCategory;

public sealed record CreateCategoryCommand(string Name, CategoryType CategoryType, Guid? ParentCategoryId);
