using LifeOS.Domain.Finance.Categories;

namespace LifeOS.UnitTests.Finance.Categories;

public class CategoryTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 9, 29, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_TopLevel_ReturnsCategory()
    {
        var category = Category.Create("Auto", CategoryType.Expense, parent: null, CreatedAtUtc);

        Assert.NotEqual(Guid.Empty, category.Id);
        Assert.Equal("Auto", category.Name);
        Assert.Equal(CategoryType.Expense, category.CategoryType);
        Assert.Null(category.ParentCategoryId);
        Assert.Equal(CreatedAtUtc, category.CreatedAtUtc);
    }

    [Fact]
    public void Create_WithParent_SetsParentCategoryId()
    {
        var parent = Category.Create("Auto", CategoryType.Expense, parent: null, CreatedAtUtc);

        var child = Category.Create("Benzina", CategoryType.Expense, parent, CreatedAtUtc);

        Assert.Equal(parent.Id, child.ParentCategoryId);
        Assert.NotEqual(parent.Id, child.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Create_WithBlankName_Throws(string? name)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => Category.Create(name!, CategoryType.Expense, parent: null, CreatedAtUtc));
    }

    [Fact]
    public void Create_TrimsName()
    {
        var category = Category.Create("  Mangiare fuori  ", CategoryType.Expense, parent: null, CreatedAtUtc);

        Assert.Equal("Mangiare fuori", category.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void Create_WithUndefinedType_Throws(int categoryType)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Category.Create("Auto", (CategoryType)categoryType, parent: null, CreatedAtUtc));
    }

    [Fact]
    public void Create_WithParentOfDifferentType_Throws()
    {
        var incomeParent = Category.Create("Stipendio", CategoryType.Income, parent: null, CreatedAtUtc);

        var exception = Assert.Throws<ArgumentException>(
            () => Category.Create("Benzina", CategoryType.Expense, incomeParent, CreatedAtUtc));

        Assert.Equal("parent", exception.ParamName);
    }

    [Fact]
    public void Create_WithSubcategoryAsParent_Throws()
    {
        var parent = Category.Create("Auto", CategoryType.Expense, parent: null, CreatedAtUtc);
        var child = Category.Create("Benzina", CategoryType.Expense, parent, CreatedAtUtc);

        var exception = Assert.Throws<ArgumentException>(
            () => Category.Create("Diesel", CategoryType.Expense, child, CreatedAtUtc));

        Assert.Equal("parent", exception.ParamName);
    }

    [Fact]
    public void Create_NormalizesCreatedAtToUtc()
    {
        var createdAt = new DateTimeOffset(2026, 9, 29, 12, 30, 0, TimeSpan.FromHours(2));

        var category = Category.Create("Auto", CategoryType.Expense, parent: null, createdAt);

        Assert.Equal(TimeSpan.Zero, category.CreatedAtUtc.Offset);
        Assert.Equal(createdAt.UtcTicks, category.CreatedAtUtc.UtcTicks);
    }
}
