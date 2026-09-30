using LifeOS.Domain.Finance.Categories;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Categories;

public class CategoryTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 9, 29, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_SetsOwningUser()
    {
        var category = Category.Create(TestUsers.A, "Auto", CategoryType.Expense, parent: null, CreatedAtUtc);

        Assert.Equal(TestUsers.A, category.UserId);
    }

    [Fact]
    public void Create_WithEmptyUserId_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => Category.Create(Guid.Empty, "Auto", CategoryType.Expense, parent: null, CreatedAtUtc));

        Assert.Equal("userId", exception.ParamName);
    }

    [Fact]
    public void Create_WithParentOwnedByAnotherUser_Throws()
    {
        var parentOfA = Category.Create(TestUsers.A, "Auto", CategoryType.Expense, parent: null, CreatedAtUtc);

        var exception = Assert.Throws<ArgumentException>(
            () => Category.Create(TestUsers.B, "Benzina", CategoryType.Expense, parentOfA, CreatedAtUtc));

        Assert.Equal("parent", exception.ParamName);
    }

    [Fact]
    public void Create_TopLevel_ReturnsCategory()
    {
        var category = Category.Create(TestUsers.A, "Auto", CategoryType.Expense, parent: null, CreatedAtUtc);

        Assert.NotEqual(Guid.Empty, category.Id);
        Assert.Equal("Auto", category.Name);
        Assert.Equal(CategoryType.Expense, category.CategoryType);
        Assert.Null(category.ParentCategoryId);
        Assert.Equal(CreatedAtUtc, category.CreatedAtUtc);
    }

    [Fact]
    public void Create_WithParent_SetsParentCategoryId()
    {
        var parent = Category.Create(TestUsers.A, "Auto", CategoryType.Expense, parent: null, CreatedAtUtc);

        var child = Category.Create(TestUsers.A, "Benzina", CategoryType.Expense, parent, CreatedAtUtc);

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
            () => Category.Create(TestUsers.A, name!, CategoryType.Expense, parent: null, CreatedAtUtc));
    }

    [Fact]
    public void Create_TrimsName()
    {
        var category = Category.Create(TestUsers.A, "  Mangiare fuori  ", CategoryType.Expense, parent: null, CreatedAtUtc);

        Assert.Equal("Mangiare fuori", category.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void Create_WithUndefinedType_Throws(int categoryType)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Category.Create(TestUsers.A, "Auto", (CategoryType)categoryType, parent: null, CreatedAtUtc));
    }

    [Fact]
    public void Create_WithParentOfDifferentType_Throws()
    {
        var incomeParent = Category.Create(TestUsers.A, "Stipendio", CategoryType.Income, parent: null, CreatedAtUtc);

        var exception = Assert.Throws<ArgumentException>(
            () => Category.Create(TestUsers.A, "Benzina", CategoryType.Expense, incomeParent, CreatedAtUtc));

        Assert.Equal("parent", exception.ParamName);
    }

    [Fact]
    public void Create_WithSubcategoryAsParent_Throws()
    {
        var parent = Category.Create(TestUsers.A, "Auto", CategoryType.Expense, parent: null, CreatedAtUtc);
        var child = Category.Create(TestUsers.A, "Benzina", CategoryType.Expense, parent, CreatedAtUtc);

        var exception = Assert.Throws<ArgumentException>(
            () => Category.Create(TestUsers.A, "Diesel", CategoryType.Expense, child, CreatedAtUtc));

        Assert.Equal("parent", exception.ParamName);
    }

    [Fact]
    public void Create_NormalizesCreatedAtToUtc()
    {
        var createdAt = new DateTimeOffset(2026, 9, 29, 12, 30, 0, TimeSpan.FromHours(2));

        var category = Category.Create(TestUsers.A, "Auto", CategoryType.Expense, parent: null, createdAt);

        Assert.Equal(TimeSpan.Zero, category.CreatedAtUtc.Offset);
        Assert.Equal(createdAt.UtcTicks, category.CreatedAtUtc.UtcTicks);
    }

    [Fact]
    public void Rename_TrimsTheName_AndKeepsTypeParentOwnerAndId()
    {
        var parent = Category.Create(TestUsers.A, "Food & Drink", CategoryType.Expense, parent: null, CreatedAtUtc);
        var child = Category.Create(TestUsers.A, "Groceries", CategoryType.Expense, parent, CreatedAtUtc);
        var id = child.Id;

        child.Rename("  Supermarket  ");

        Assert.Equal("Supermarket", child.Name);
        Assert.Equal(id, child.Id);
        Assert.Equal(TestUsers.A, child.UserId);
        Assert.Equal(CategoryType.Expense, child.CategoryType);
        Assert.Equal(parent.Id, child.ParentCategoryId);
        Assert.Equal(CreatedAtUtc, child.CreatedAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rename_WithBlankName_ThrowsAndKeepsTheName(string? name)
    {
        var category = Category.Create(TestUsers.A, "Travel", CategoryType.Expense, parent: null, CreatedAtUtc);

        var exception = Assert.ThrowsAny<ArgumentException>(() => category.Rename(name!));

        Assert.Equal("name", exception.ParamName);
        Assert.Equal("Travel", category.Name);
    }
}
