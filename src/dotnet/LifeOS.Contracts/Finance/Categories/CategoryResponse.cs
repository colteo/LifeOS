namespace LifeOS.Contracts.Finance.Categories;

public sealed record CategoryResponse(
    Guid Id,
    string Name,
    string Type,
    Guid? ParentCategoryId,
    DateTimeOffset CreatedAtUtc);
