using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Categories.DeleteCategory;
using LifeOS.Application.Finance.Categories.RenameCategory;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Categories;

public class CategoryManagementHandlersTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);

    private readonly InMemoryCategoryRepository _categories = new();
    private readonly InMemoryTransactionRepository _transactions = new();

    // ---- RenameCategory ----

    [Fact]
    public async Task Rename_TopLevel_TrimsAndKeepsTypeAndId()
    {
        var foodAndDrink = Add(TestUsers.A, "Food & Drink", CategoryType.Expense);

        var result = await RenameAsync(TestUsers.A, foodAndDrink.Id, "  Food  ");

        Assert.Equal(RenameCategoryStatus.Renamed, result.Status);
        Assert.Equal((foodAndDrink.Id, "Food", CategoryType.Expense, (Guid?)null), (result.Category!.Id, result.Category.Name, result.Category.CategoryType, result.Category.ParentCategoryId));
        Assert.Equal("Food", _categories.Stored(foodAndDrink.Id).Name);
    }

    [Fact]
    public async Task Rename_Subcategory_KeepsItsParent()
    {
        var foodAndDrink = Add(TestUsers.A, "Food & Drink", CategoryType.Expense);
        var groceries = Add(TestUsers.A, "Groceries", CategoryType.Expense, foodAndDrink);

        var result = await RenameAsync(TestUsers.A, groceries.Id, "Supermarket");

        Assert.Equal(RenameCategoryStatus.Renamed, result.Status);
        var stored = _categories.Stored(groceries.Id);
        Assert.Equal(("Supermarket", (Guid?)foodAndDrink.Id), (stored.Name, stored.ParentCategoryId));
    }

    [Fact]
    public async Task Rename_CaseOnlyChangeOfItself_IsAllowed()
    {
        var travel = Add(TestUsers.A, "travel", CategoryType.Expense);

        var result = await RenameAsync(TestUsers.A, travel.Id, "Travel");

        Assert.Equal(RenameCategoryStatus.Renamed, result.Status);
        Assert.Equal("Travel", _categories.Stored(travel.Id).Name);
    }

    [Fact]
    public async Task Rename_ToASiblingsNameIgnoringCase_IsADuplicateAndChangesNothing()
    {
        var foodAndDrink = Add(TestUsers.A, "Food & Drink", CategoryType.Expense);
        Add(TestUsers.A, "Groceries", CategoryType.Expense, foodAndDrink);
        var eatingOut = Add(TestUsers.A, "Eating out", CategoryType.Expense, foodAndDrink);

        var result = await RenameAsync(TestUsers.A, eatingOut.Id, "GROCERIES");

        Assert.Equal(RenameCategoryStatus.DuplicateName, result.Status);
        Assert.Equal("Eating out", _categories.Stored(eatingOut.Id).Name);
    }

    [Fact]
    public async Task Rename_ToATopLevelSiblingsName_IsADuplicate()
    {
        Add(TestUsers.A, "Travel", CategoryType.Expense);
        var leisure = Add(TestUsers.A, "Leisure", CategoryType.Expense);

        Assert.Equal(RenameCategoryStatus.DuplicateName, (await RenameAsync(TestUsers.A, leisure.Id, "travel")).Status);
    }

    [Fact]
    public async Task Rename_ToANameUsedUnderAnotherParentOrType_IsAllowed()
    {
        var shopping = Add(TestUsers.A, "Shopping", CategoryType.Expense);
        Add(TestUsers.A, "Groceries", CategoryType.Expense, shopping);
        var foodAndDrink = Add(TestUsers.A, "Food & Drink", CategoryType.Expense);
        var eatingOut = Add(TestUsers.A, "Eating out", CategoryType.Expense, foodAndDrink);
        Add(TestUsers.A, "Gifts", CategoryType.Income);
        var bonus = Add(TestUsers.A, "Bonus", CategoryType.Expense);

        Assert.Equal(RenameCategoryStatus.Renamed, (await RenameAsync(TestUsers.A, eatingOut.Id, "Groceries")).Status);
        Assert.Equal(RenameCategoryStatus.Renamed, (await RenameAsync(TestUsers.A, bonus.Id, "Gifts")).Status);
    }

    [Fact]
    public async Task Rename_AnotherUsersCategory_IsNotFoundAndUnchanged()
    {
        var ofB = Add(TestUsers.B, "Travel", CategoryType.Expense);

        var result = await RenameAsync(TestUsers.A, ofB.Id, "Mine");

        Assert.Equal(RenameCategoryStatus.NotFound, result.Status);
        Assert.Equal("Travel", _categories.Stored(ofB.Id).Name);
    }

    [Fact]
    public async Task Rename_MissingCategory_IsNotFound()
    {
        Assert.Equal(RenameCategoryStatus.NotFound, (await RenameAsync(TestUsers.A, Guid.CreateVersion7(), "X")).Status);
    }

    [Fact]
    public async Task Rename_LosingToAConcurrentSiblingWithTheSameName_IsADuplicate()
    {
        // Another request takes the name after the handler's sibling check (the index decides).
        var travel = Add(TestUsers.A, "Travel", CategoryType.Expense);
        _categories.BeforeWrite = () =>
            _categories.Categories.Add(Category.Create(TestUsers.A, "Trips", CategoryType.Expense, parent: null, Now));

        var result = await RenameAsync(TestUsers.A, travel.Id, "Trips");

        Assert.Equal(RenameCategoryStatus.DuplicateName, result.Status);
        Assert.Equal("Travel", _categories.Stored(travel.Id).Name);
    }

    [Fact]
    public async Task Rename_CategoryDeletedConcurrently_IsNotFound()
    {
        var travel = Add(TestUsers.A, "Travel", CategoryType.Expense);
        _categories.BeforeWrite = () => _categories.Categories.Clear();

        Assert.Equal(RenameCategoryStatus.NotFound, (await RenameAsync(TestUsers.A, travel.Id, "Trips")).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Rename_WithBlankName_ThrowsAndChangesNothing(string name)
    {
        var travel = Add(TestUsers.A, "Travel", CategoryType.Expense);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => RenameAsync(TestUsers.A, travel.Id, name));

        Assert.Equal("Travel", _categories.Stored(travel.Id).Name);
    }

    // ---- DeleteCategory ----

    [Fact]
    public async Task Delete_UnusedSubcategory_DeletesOnlyIt()
    {
        var foodAndDrink = Add(TestUsers.A, "Food & Drink", CategoryType.Expense);
        var groceries = Add(TestUsers.A, "Groceries", CategoryType.Expense, foodAndDrink);

        Assert.Equal(DeleteCategoryResult.Deleted, await DeleteAsync(TestUsers.A, groceries.Id));
        Assert.Equal(foodAndDrink.Id, Assert.Single(_categories.Categories).Id);
    }

    [Fact]
    public async Task Delete_UnusedTopLevelWithoutChildren_DeletesIt()
    {
        var travel = Add(TestUsers.A, "Travel", CategoryType.Expense);
        var salary = Add(TestUsers.A, "Salary", CategoryType.Income);

        Assert.Equal(DeleteCategoryResult.Deleted, await DeleteAsync(TestUsers.A, travel.Id));
        Assert.Equal(salary.Id, Assert.Single(_categories.Categories).Id);
    }

    [Fact]
    public async Task Delete_ParentWithChildren_IsBlockedAndDeletesNothing()
    {
        var foodAndDrink = Add(TestUsers.A, "Food & Drink", CategoryType.Expense);
        Add(TestUsers.A, "Groceries", CategoryType.Expense, foodAndDrink);

        Assert.Equal(DeleteCategoryResult.HasSubcategories, await DeleteAsync(TestUsers.A, foodAndDrink.Id));
        Assert.Equal(2, _categories.Categories.Count);
    }

    [Fact]
    public async Task Delete_SubcategoryUsedByATransaction_IsBlocked()
    {
        var foodAndDrink = Add(TestUsers.A, "Food & Drink", CategoryType.Expense);
        var groceries = Add(TestUsers.A, "Groceries", CategoryType.Expense, foodAndDrink);
        AddExpense(TestUsers.A, groceries);

        Assert.Equal(DeleteCategoryResult.InUse, await DeleteAsync(TestUsers.A, groceries.Id));
        Assert.Equal(2, _categories.Categories.Count);
        Assert.Single(_transactions.Transactions);
    }

    [Fact]
    public async Task Delete_TopLevelUsedByATransaction_IsBlocked()
    {
        var travel = Add(TestUsers.A, "Travel", CategoryType.Expense);
        AddExpense(TestUsers.A, travel);

        Assert.Equal(DeleteCategoryResult.InUse, await DeleteAsync(TestUsers.A, travel.Id));
        Assert.Single(_categories.Categories);
    }

    [Fact]
    public async Task Delete_AnotherUsersCategory_IsNotFoundAndKeepsIt()
    {
        var ofB = Add(TestUsers.B, "Travel", CategoryType.Expense);

        Assert.Equal(DeleteCategoryResult.NotFound, await DeleteAsync(TestUsers.A, ofB.Id));
        Assert.Single(_categories.Categories);
    }

    [Fact]
    public async Task Delete_MissingCategory_IsNotFound()
    {
        Assert.Equal(DeleteCategoryResult.NotFound, await DeleteAsync(TestUsers.A, Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Delete_ChildCreatedAfterTheCheck_IsReportedAsHasSubcategories()
    {
        // The parent foreign key is the backstop for a subcategory created concurrently.
        var travel = Add(TestUsers.A, "Travel", CategoryType.Expense);
        _categories.BeforeWrite = () =>
            _categories.Categories.Add(Category.Create(TestUsers.A, "Hotels", CategoryType.Expense, travel, Now));

        Assert.Equal(DeleteCategoryResult.HasSubcategories, await DeleteAsync(TestUsers.A, travel.Id));
        Assert.Equal(2, _categories.Categories.Count);
    }

    [Fact]
    public async Task Delete_TransactionCreatedAfterTheCheck_IsReportedAsInUse()
    {
        var travel = Add(TestUsers.A, "Travel", CategoryType.Expense);
        _categories.RejectDeleteWith = CategoryDeleteOutcome.InUse;

        Assert.Equal(DeleteCategoryResult.InUse, await DeleteAsync(TestUsers.A, travel.Id));
        Assert.Single(_categories.Categories);
    }

    [Fact]
    public async Task Delete_CategoryDeletedConcurrently_IsNotFound()
    {
        var travel = Add(TestUsers.A, "Travel", CategoryType.Expense);
        _categories.BeforeWrite = () => _categories.Categories.Clear();

        Assert.Equal(DeleteCategoryResult.NotFound, await DeleteAsync(TestUsers.A, travel.Id));
    }

    private Task<RenameCategoryResult> RenameAsync(Guid userId, Guid categoryId, string name) =>
        new RenameCategoryHandler(_categories).HandleAsync(
            userId,
            new RenameCategoryCommand(categoryId, name),
            CancellationToken.None);

    private Task<DeleteCategoryResult> DeleteAsync(Guid userId, Guid categoryId) =>
        new DeleteCategoryHandler(_categories, _transactions).HandleAsync(userId, categoryId, CancellationToken.None);

    private Category Add(Guid userId, string name, CategoryType categoryType, Category? parent = null)
    {
        var category = Category.Create(userId, name, categoryType, parent, Now);
        _categories.Categories.Add(category);

        return category;
    }

    private void AddExpense(Guid userId, Category category) =>
        _transactions.Transactions.Add(
            Transaction.CreateExpense(userId, Guid.CreateVersion7(), category.Id, 10m, "EUR", Now, null, Now));
}
