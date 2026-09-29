using LifeOS.Domain.Finance.Categories;

namespace LifeOS.Application.Finance.Categories.GetCategories;

public sealed record CategorySummary(
    Guid Id,
    string Name,
    CategoryType CategoryType,
    Guid? ParentCategoryId,
    DateTimeOffset CreatedAtUtc);
