using LifeOS.Application.Finance.Categories;
using LifeOS.Domain.Finance.Categories;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Categories;

public class StarterCategoriesTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    // ---- Catalog shape ----

    [Fact]
    public void All_Has37EntriesIn8ExpenseParents23ExpenseChildrenAnd6IncomeTopLevel()
    {
        var expense = StarterCategories.All.Where(starter => starter.CategoryType == CategoryType.Expense).ToList();
        var income = StarterCategories.All.Where(starter => starter.CategoryType == CategoryType.Income).ToList();

        Assert.Equal(8, expense.Count);
        Assert.Equal(23, expense.Sum(starter => starter.Children.Count));
        Assert.Equal(6, income.Count);
        Assert.All(income, starter => Assert.Empty(starter.Children));
        Assert.Equal(37, StarterCategories.All.Count + StarterCategories.All.Sum(starter => starter.Children.Count));
    }

    [Fact]
    public void All_HasNoDuplicateTopLevelNamesWithinAType_IgnoringCase()
    {
        var keys = StarterCategories.All.Select(starter => (starter.CategoryType, starter.Name.ToLowerInvariant())).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void All_HasNoDuplicateSiblingNames_IgnoringCase()
    {
        Assert.All(StarterCategories.All, starter =>
            Assert.Equal(starter.Children.Count, starter.Children.Select(name => name.ToLowerInvariant()).Distinct().Count()));
    }

    [Fact]
    public void All_NamesAreTrimmedAndNotEmpty()
    {
        var names = StarterCategories.All.SelectMany(starter => starter.Children.Prepend(starter.Name));

        Assert.All(names, name =>
        {
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Equal(name.Trim(), name);
        });
    }

    [Fact]
    public void CreatedTree_HasMaxDepthTwo_ChildrenOfExistingParents_AndMatchingTypes()
    {
        var created = StarterCategories.CreateMissing(TestUsers.A, [], CreatedAtUtc);
        var byId = created.ToDictionary(category => category.Id);

        Assert.All(created.Where(category => category.ParentCategoryId is not null), child =>
        {
            var parent = byId[child.ParentCategoryId!.Value];
            Assert.Null(parent.ParentCategoryId);
            Assert.Equal(parent.CategoryType, child.CategoryType);
        });
    }

    [Theory]
    [InlineData("Food & Drink", new[] { "Groceries", "Eating out", "Bars & cafes" })]
    [InlineData("Health & Fitness", new[] { "Medical", "Pharmacy", "Gym & sports" })]
    [InlineData("Travel", new[] { "Accommodation", "Flights & long-distance transport" })]
    public void All_ExpenseParentsHaveTheirChildren(string parent, string[] children)
    {
        var starter = Assert.Single(StarterCategories.All, starter => starter.Name == parent);

        Assert.Equal(CategoryType.Expense, starter.CategoryType);
        Assert.Equal(children, starter.Children);
    }

    [Fact]
    public void All_SalaryIsIncomeTopLevel()
    {
        var salary = Assert.Single(StarterCategories.All, starter => starter.Name == "Salary");

        Assert.Equal(CategoryType.Income, salary.CategoryType);
        Assert.Empty(salary.Children);
    }

    // ---- CreateMissing / IsComplete ----

    [Fact]
    public void CreateMissing_WithNoCategories_CreatesTheWholeTreeForTheUser()
    {
        var created = StarterCategories.CreateMissing(TestUsers.A, [], CreatedAtUtc);

        Assert.Equal(37, created.Count);
        Assert.All(created, category =>
        {
            Assert.Equal(TestUsers.A, category.UserId);
            Assert.Equal(CreatedAtUtc, category.CreatedAtUtc);
        });
        Assert.Equal(ExpectedTree(), Tree(created));
        Assert.False(StarterCategories.IsComplete(TestUsers.A, []));
        Assert.True(StarterCategories.IsComplete(TestUsers.A, created));
    }

    [Fact]
    public void CreateMissing_AfterTheWholeTreeExists_CreatesNothing()
    {
        var existing = StarterCategories.CreateMissing(TestUsers.A, [], CreatedAtUtc);

        Assert.Empty(StarterCategories.CreateMissing(TestUsers.A, existing, CreatedAtUtc));
    }

    [Fact]
    public void CreateMissing_ExistingParentIgnoringCase_IsReusedAndSuppressesOnlyThatParent()
    {
        var foodAndDrink = TopLevel("food & drink", CategoryType.Expense);

        var created = StarterCategories.CreateMissing(TestUsers.A, [foodAndDrink], CreatedAtUtc);

        Assert.Equal(36, created.Count);
        Assert.DoesNotContain(created, category => SameName(category.Name, "Food & Drink"));
        Assert.Equal(
            ["Bars & cafes", "Eating out", "Groceries"],
            created.Where(category => category.ParentCategoryId == foodAndDrink.Id).Select(category => category.Name).Order());
    }

    [Fact]
    public void CreateMissing_ExistingChildUnderTheCorrectParent_IsNotCreatedAgain()
    {
        var foodAndDrink = TopLevel("Food & Drink", CategoryType.Expense);
        var groceries = ChildOf(foodAndDrink, "GROCERIES");

        var created = StarterCategories.CreateMissing(TestUsers.A, [foodAndDrink, groceries], CreatedAtUtc);

        Assert.Equal(35, created.Count);
        Assert.DoesNotContain(created, category => SameName(category.Name, "Groceries"));
    }

    [Fact]
    public void CreateMissing_SameChildNameUnderAnotherParent_DoesNotSuppressIt()
    {
        var shopping = TopLevel("Shopping", CategoryType.Expense);
        var groceriesUnderShopping = ChildOf(shopping, "Groceries");

        var created = StarterCategories.CreateMissing(TestUsers.A, [shopping, groceriesUnderShopping], CreatedAtUtc);

        var foodAndDrink = Assert.Single(created, category => category.Name == "Food & Drink");
        Assert.Single(created, category => category.Name == "Groceries" && category.ParentCategoryId == foodAndDrink.Id);
        Assert.DoesNotContain(created, category => category.ParentCategoryId == shopping.Id && category.Name == "Groceries");
    }

    [Fact]
    public void CreateMissing_SameNameAtTopLevel_DoesNotSuppressAChild()
    {
        var groceries = TopLevel("Groceries", CategoryType.Expense);

        var created = StarterCategories.CreateMissing(TestUsers.A, [groceries], CreatedAtUtc);

        var foodAndDrink = Assert.Single(created, category => category.Name == "Food & Drink");
        Assert.Single(created, category => category.Name == "Groceries" && category.ParentCategoryId == foodAndDrink.Id);
    }

    [Fact]
    public void CreateMissing_SameNameNested_DoesNotSuppressAParent()
    {
        var leisure = TopLevel("Leisure", CategoryType.Expense);
        var travelUnderLeisure = ChildOf(leisure, "Travel");

        var created = StarterCategories.CreateMissing(TestUsers.A, [leisure, travelUnderLeisure], CreatedAtUtc);

        Assert.Single(created, category => category.Name == "Travel" && category.ParentCategoryId is null);
    }

    [Fact]
    public void CreateMissing_SameNameOfTheOtherType_DoesNotSuppressIt()
    {
        var salaryAsExpense = TopLevel("Salary", CategoryType.Expense);

        var created = StarterCategories.CreateMissing(TestUsers.A, [salaryAsExpense], CreatedAtUtc);

        Assert.Single(created, category => category is { Name: "Salary", CategoryType: CategoryType.Income });
    }

    [Fact]
    public void CreateMissing_IgnoresCustomCategoriesAndLeavesThemUntouched()
    {
        var pets = TopLevel("Pets", CategoryType.Expense);
        var vet = ChildOf(pets, "Vet");

        var created = StarterCategories.CreateMissing(TestUsers.A, [pets, vet], CreatedAtUtc);

        Assert.Equal(37, created.Count);
        Assert.DoesNotContain(created, category => category.Id == pets.Id || category.Id == vet.Id);
        Assert.DoesNotContain(created, category => category.ParentCategoryId == pets.Id);
    }

    [Fact]
    public void CreateMissing_AnotherUsersCategories_HaveNoEffect()
    {
        var othersTree = StarterCategories.CreateMissing(TestUsers.B, [], CreatedAtUtc);

        var created = StarterCategories.CreateMissing(TestUsers.A, othersTree, CreatedAtUtc);

        Assert.Equal(37, created.Count);
        Assert.All(created, category => Assert.Equal(TestUsers.A, category.UserId));
        Assert.Empty(created.Select(category => category.ParentCategoryId).Intersect(othersTree.Select(category => (Guid?)category.Id)));
        Assert.False(StarterCategories.IsComplete(TestUsers.A, othersTree));
    }

    [Fact]
    public void CreateMissing_PartialTree_IsCompleted()
    {
        var full = StarterCategories.CreateMissing(TestUsers.A, [], CreatedAtUtc);
        // Keep all parents and only the first child of each parent.
        var partial = full
            .Where(category => category.ParentCategoryId is null
                || full.First(candidate => candidate.ParentCategoryId == category.ParentCategoryId).Id == category.Id)
            .ToList();

        var created = StarterCategories.CreateMissing(TestUsers.A, partial, CreatedAtUtc);

        Assert.Equal(37 - partial.Count, created.Count);
        Assert.All(created, category => Assert.NotNull(category.ParentCategoryId));
        Assert.All(created, category => Assert.Contains(partial, parent => parent.Id == category.ParentCategoryId));
        Assert.False(StarterCategories.IsComplete(TestUsers.A, partial));
        Assert.True(StarterCategories.IsComplete(TestUsers.A, partial.Concat(created)));
        Assert.Equal(ExpectedTree(), Tree([.. partial, .. created]));
    }

    // (type, parent name or null, name), sorted: the tree independent of ids and order.
    internal static IReadOnlyList<(CategoryType Type, string? Parent, string Name)> ExpectedTree() =>
        StarterCategories.All
            .SelectMany(starter => starter.Children
                .Select(child => (starter.CategoryType, (string?)starter.Name, child))
                .Prepend((starter.CategoryType, null, starter.Name)))
            .Order()
            .ToList();

    internal static IReadOnlyList<(CategoryType Type, string? Parent, string Name)> Tree(IReadOnlyCollection<Category> categories) =>
        categories
            .Select(category => (
                category.CategoryType,
                category.ParentCategoryId is { } parentId ? categories.Single(parent => parent.Id == parentId).Name : null,
                category.Name))
            .Order()
            .ToList();

    private static bool SameName(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static Category TopLevel(string name, CategoryType categoryType) =>
        Category.Create(TestUsers.A, name, categoryType, parent: null, CreatedAtUtc);

    private static Category ChildOf(Category parent, string name) =>
        Category.Create(TestUsers.A, name, parent.CategoryType, parent, CreatedAtUtc);
}
