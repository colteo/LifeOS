using LifeOS.Domain.Finance.Categories;

namespace LifeOS.Application.Finance.Categories.CreateCategory;

public enum CreateCategoryStatus
{
    Created,
    ParentNotFound,
    DuplicateName
}

public sealed record CreatedCategory(
    Guid Id,
    string Name,
    CategoryType CategoryType,
    Guid? ParentCategoryId,
    DateTimeOffset CreatedAtUtc);

public sealed record CreateCategoryResult(CreateCategoryStatus Status, CreatedCategory? Category)
{
    public static CreateCategoryResult Created(CreatedCategory category) =>
        new(CreateCategoryStatus.Created, category);

    public static CreateCategoryResult ParentNotFound() =>
        new(CreateCategoryStatus.ParentNotFound, null);

    public static CreateCategoryResult DuplicateName() =>
        new(CreateCategoryStatus.DuplicateName, null);
}
