using LifeOS.Application.Finance.Categories;
using LifeOS.Domain.Finance.Categories;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Categories;

public class StarterCategoriesTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void All_ContainsTenExpenseAndFiveIncomeCategories()
    {
        Assert.Equal(15, StarterCategories.All.Count);
        Assert.Equal(10, StarterCategories.All.Count(starter => starter.CategoryType == CategoryType.Expense));
        Assert.Equal(5, StarterCategories.All.Count(starter => starter.CategoryType == CategoryType.Income));
    }

    [Fact]
    public void All_HasAltroForBothTypes()
    {
        Assert.Contains(new StarterCategory("Altro", CategoryType.Expense), StarterCategories.All);
        Assert.Contains(new StarterCategory("Altro", CategoryType.Income), StarterCategories.All);
    }

    [Fact]
    public void All_HasNoDuplicateSiblings()
    {
        var keys = StarterCategories.All.Select(starter => (starter.CategoryType, starter.Name.ToLowerInvariant())).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Missing_WithNoCategories_ReturnsTheWholeCatalog()
    {
        Assert.Equal(StarterCategories.All, StarterCategories.Missing([]));
    }

    [Fact]
    public void Missing_IgnoresCustomCategories()
    {
        var bar = Category.Create(TestUsers.A, "Bar", CategoryType.Expense, parent: null, CreatedAtUtc);

        Assert.Equal(15, StarterCategories.Missing([bar]).Count);
    }

    [Fact]
    public void Missing_TreatsSameTypeTopLevelNameIgnoringCaseAsPresent()
    {
        var casa = Category.Create(TestUsers.A, "casa", CategoryType.Expense, parent: null, CreatedAtUtc);

        var missing = StarterCategories.Missing([casa]);

        Assert.Equal(14, missing.Count);
        Assert.DoesNotContain(new StarterCategory("Casa", CategoryType.Expense), missing);
    }

    [Fact]
    public void Missing_DoesNotMatchTheOtherType()
    {
        var altroExpense = Category.Create(TestUsers.A, "Altro", CategoryType.Expense, parent: null, CreatedAtUtc);

        var missing = StarterCategories.Missing([altroExpense]);

        Assert.Contains(new StarterCategory("Altro", CategoryType.Income), missing);
        Assert.DoesNotContain(new StarterCategory("Altro", CategoryType.Expense), missing);
    }

    [Fact]
    public void Missing_DoesNotMatchSubcategories()
    {
        var auto = Category.Create(TestUsers.A, "Auto", CategoryType.Expense, parent: null, CreatedAtUtc);
        var casaUnderAuto = Category.Create(TestUsers.A, "Casa", CategoryType.Expense, auto, CreatedAtUtc);

        var missing = StarterCategories.Missing([auto, casaUnderAuto]);

        Assert.Contains(new StarterCategory("Casa", CategoryType.Expense), missing);
        Assert.DoesNotContain(new StarterCategory("Auto", CategoryType.Expense), missing);
    }
}
