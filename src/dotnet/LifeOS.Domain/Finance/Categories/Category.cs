namespace LifeOS.Domain.Finance.Categories;

public sealed class Category
{
    private Category(
        Guid id,
        Guid userId,
        string name,
        CategoryType categoryType,
        Guid? parentCategoryId,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        UserId = userId;
        Name = name;
        CategoryType = categoryType;
        ParentCategoryId = parentCategoryId;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    // The owning LifeOS user.
    public Guid UserId { get; }

    // Current metadata, not a historical snapshot: transactions show the category's current name.
    public string Name { get; private set; }

    public CategoryType CategoryType { get; }

    public Guid? ParentCategoryId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static Category Create(
        Guid userId,
        string name,
        CategoryType categoryType,
        Category? parent,
        DateTimeOffset createdAtUtc)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        var normalizedName = NormalizeName(name);

        if (!Enum.IsDefined(categoryType))
        {
            throw new ArgumentOutOfRangeException(nameof(categoryType), categoryType, "Category type is not supported.");
        }

        if (parent is not null)
        {
            if (parent.UserId != userId)
            {
                throw new ArgumentException("A subcategory must belong to the same user as its parent category.", nameof(parent));
            }

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
            userId,
            normalizedName,
            categoryType,
            parent?.Id,
            createdAtUtc.ToUniversalTime());
    }

    // Type, parent, owner and id never change; sibling-name uniqueness is enforced by Application
    // and the database.
    public void Rename(string name)
    {
        Name = NormalizeName(name);
    }

    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return name.Trim();
    }
}
