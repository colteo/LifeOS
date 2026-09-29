namespace LifeOS.Domain.Finance.Categories;

public sealed class Category
{
    private Category(
        Guid id,
        string name,
        CategoryType categoryType,
        Guid? parentCategoryId,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        Name = name;
        CategoryType = categoryType;
        ParentCategoryId = parentCategoryId;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    public string Name { get; }

    public CategoryType CategoryType { get; }

    public Guid? ParentCategoryId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static Category Create(
        string name,
        CategoryType categoryType,
        Category? parent,
        DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!Enum.IsDefined(categoryType))
        {
            throw new ArgumentOutOfRangeException(nameof(categoryType), categoryType, "Category type is not supported.");
        }

        if (parent is not null)
        {
            if (parent.CategoryType != categoryType)
            {
                throw new ArgumentException("A subcategory must have the same type as its parent category.", nameof(parent));
            }

            if (parent.ParentCategoryId is not null)
            {
                throw new ArgumentException("Only one level of subcategories is supported.", nameof(parent));
            }
        }

        // The id is generated here, so a new category can never be its own parent.
        return new Category(
            Guid.CreateVersion7(),
            name.Trim(),
            categoryType,
            parent?.Id,
            createdAtUtc.ToUniversalTime());
    }
}
