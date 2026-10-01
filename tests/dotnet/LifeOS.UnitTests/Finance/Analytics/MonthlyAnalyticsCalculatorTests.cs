using LifeOS.Application.Finance.Analytics;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Analytics;

public class MonthlyAnalyticsCalculatorTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly Category _foodAndDrink = Expense("Food & Drink");
    private readonly Category _groceries;
    private readonly Category _eatingOut;
    private readonly Category _barsAndCafes;
    private readonly Category _travel = Expense("Travel");
    private readonly Category _salary = Category.Create(TestUsers.A, "Salary", CategoryType.Income, parent: null, At);

    public MonthlyAnalyticsCalculatorTests()
    {
        _groceries = Child("Groceries", _foodAndDrink);
        _eatingOut = Child("Eating out", _foodAndDrink);
        _barsAndCafes = Child("Bars & cafes", _foodAndDrink);
    }

    private IReadOnlyList<Category> Categories => [_foodAndDrink, _groceries, _eatingOut, _barsAndCafes, _travel, _salary];

    [Fact]
    public void NoTransactions_GiveNoCurrencyBlocks()
    {
        Assert.Empty(MonthlyAnalyticsCalculator.Build([], Categories));
    }

    [Fact]
    public void ExpensesOnly_GiveExpensesAndANegativeNetFlow()
    {
        var eur = Single([Spend(_travel, 80m), Spend(_groceries, 20.5m)]);

        Assert.Equal(100.5m, eur.Expenses);
        Assert.Equal(0m, eur.Income);
        Assert.Equal(-100.5m, eur.NetFlow);
    }

    [Fact]
    public void IncomeOnly_GivesIncomeAPositiveNetFlowAndNoExpenseCategories()
    {
        var eur = Single([Earn(1500m)]);

        Assert.Equal(0m, eur.Expenses);
        Assert.Equal(1500m, eur.Income);
        Assert.Equal(1500m, eur.NetFlow);
        Assert.Empty(eur.ExpenseCategories);
    }

    [Theory]
    [InlineData(1500, 1357, 143)]
    [InlineData(1000, 1250.25, -250.25)]
    [InlineData(500, 500, 0)]
    public void NetFlow_IsIncomeMinusExpenses(decimal income, decimal expenses, decimal netFlow)
    {
        var eur = Single([Earn(income), Spend(_travel, expenses)]);

        Assert.Equal(expenses, eur.Expenses);
        Assert.Equal(income, eur.Income);
        Assert.Equal(netFlow, eur.NetFlow);
    }

    [Fact]
    public void Transfers_AreExcluded_AndATransferOnlyCurrencyGetsNoBlock()
    {
        var result = MonthlyAnalyticsCalculator.Build(
            [Spend(_travel, 40m), Earn(100m), Move(250m), Move(999m, "GBP")],
            Categories);

        var eur = Assert.Single(result);
        Assert.Equal("EUR", eur.Currency);
        Assert.Equal(40m, eur.Expenses);
        Assert.Equal(100m, eur.Income);
        Assert.Equal(60m, eur.NetFlow);
    }

    [Fact]
    public void Currencies_AreSeparateBlocks_NeverCombined_OrderedByCode()
    {
        var result = MonthlyAnalyticsCalculator.Build(
            [Spend(_travel, 10m, "USD"), Spend(_travel, 30m), Earn(200m, "USD"), Spend(_groceries, 5m, "USD")],
            Categories);

        Assert.Equal(["EUR", "USD"], result.Select(block => block.Currency));
        var (eur, usd) = (result[0], result[1]);
        Assert.Equal((30m, 0m), (eur.Expenses, eur.Income));
        Assert.Equal((15m, 200m, 185m), (usd.Expenses, usd.Income, usd.NetFlow));
        Assert.Equal([30m], eur.ExpenseCategories.Select(group => group.Amount));
        Assert.Equal([10m, 5m], usd.ExpenseCategories.Select(group => group.Amount));
    }

    [Fact]
    public void ExpenseOnAParent_CountsInItsTotalAndDirectAmount()
    {
        var group = Assert.Single(Single([Spend(_foodAndDrink, 30m)]).ExpenseCategories);

        Assert.Equal((_foodAndDrink.Id, "Food & Drink"), (group.CategoryId!.Value, group.Name));
        Assert.Equal(30m, group.Amount);
        Assert.Equal(30m, group.DirectAmount);
        Assert.Empty(group.Subcategories);
    }

    [Fact]
    public void ChildExpenses_RollIntoTheParent_WithDirectAndSortedSubcategories()
    {
        var eur = Single(
        [
            Spend(_foodAndDrink, 30m),
            Spend(_groceries, 100m),
            Spend(_groceries, 90m),
            Spend(_eatingOut, 120m),
            Spend(_barsAndCafes, 55m)
        ]);

        var group = Assert.Single(eur.ExpenseCategories);
        Assert.Equal(395m, group.Amount);
        Assert.Equal(30m, group.DirectAmount);
        Assert.Equal(
            [("Groceries", 190m), ("Eating out", 120m), ("Bars & cafes", 55m)],
            group.Subcategories.Select(subcategory => (subcategory.Name, subcategory.Amount)));
        Assert.Equal(
            [_groceries.Id, _eatingOut.Id, _barsAndCafes.Id],
            group.Subcategories.Select(subcategory => subcategory.CategoryId));
    }

    [Fact]
    public void ChildOnlyGroup_HasNoDirectAmount()
    {
        var group = Assert.Single(Single([Spend(_eatingOut, 12m)]).ExpenseCategories);

        Assert.Equal(_foodAndDrink.Id, group.CategoryId);
        Assert.Equal((12m, 0m), (group.Amount, group.DirectAmount));
    }

    [Fact]
    public void Groups_AreSortedByAmountThenName_AndAlwaysAddUpToExpenses()
    {
        var pets = Expense("pets");
        var vet = Child("Vet", pets);
        var result = MonthlyAnalyticsCalculator.Build(
            [
                Spend(_travel, 50m),
                Spend(vet, 30m),
                Spend(pets, 20m),
                Spend(_groceries, 12.34m),
                Spend(_eatingOut, 7.66m),
                Spend(_foodAndDrink, 30m)
            ],
            [.. Categories, pets, vet]);

        var eur = Assert.Single(result);
        // Food & Drink, pets and Travel all total 50: then by name, ignoring case.
        Assert.Equal(["Food & Drink", "pets", "Travel"], eur.ExpenseCategories.Select(group => group.Name));
        Assert.Equal(eur.Expenses, eur.ExpenseCategories.Sum(group => group.Amount));
        Assert.All(eur.ExpenseCategories, group =>
            Assert.Equal(group.Amount, group.DirectAmount + group.Subcategories.Sum(subcategory => subcategory.Amount)));
    }

    [Fact]
    public void CustomCategories_AreGroupedLikeStarterOnes()
    {
        var pets = Expense("Pets");
        var vet = Child("Vet", pets);
        var food = Child("Food", pets);

        var group = Assert.Single(Single([Spend(vet, 80m), Spend(food, 25m)], [pets, vet, food]).ExpenseCategories);

        Assert.Equal(("Pets", 105m, 0m), (group.Name, group.Amount, group.DirectAmount));
        Assert.Equal(["Vet", "Food"], group.Subcategories.Select(subcategory => subcategory.Name));
    }

    [Fact]
    public void RenamedCategories_AppearUnderTheirCurrentNames()
    {
        var expense = Spend(_groceries, 10m);
        _foodAndDrink.Rename("Food");
        _groceries.Rename("Supermarket");

        var group = Assert.Single(Single([expense]).ExpenseCategories);

        Assert.Equal("Food", group.Name);
        Assert.Equal("Supermarket", Assert.Single(group.Subcategories).Name);
    }

    [Fact]
    public void IncomeCategories_NeverAppearInTheExpenseBreakdown()
    {
        var eur = Single([Earn(100m), Spend(_travel, 10m)]);

        Assert.Equal(["Travel"], eur.ExpenseCategories.Select(group => group.Name));
    }

    [Fact]
    public void ChildWhoseParentIsMissing_FormsItsOwnGroup_NothingIsLost()
    {
        // The parent is not among the user's categories: the child stands on its own.
        var eur = Single([Spend(_groceries, 40m), Spend(_travel, 10m)], [_groceries, _travel]);

        var groceries = Assert.Single(eur.ExpenseCategories, group => group.CategoryId == _groceries.Id);
        Assert.Equal(("Groceries", 40m, 40m), (groceries.Name, groceries.Amount, groceries.DirectAmount));
        Assert.Equal(50m, eur.ExpenseCategories.Sum(group => group.Amount));
    }

    [Fact]
    public void ExpenseWithAnUnknownCategory_GoesToOneUnknownGroupWithoutAnId()
    {
        var eur = Single(
            [Spend(_travel, 10m), SpendOn(Guid.CreateVersion7(), 3m), SpendOn(Guid.CreateVersion7(), 4m)]);

        var unknown = Assert.Single(eur.ExpenseCategories, group => group.CategoryId is null);
        Assert.Equal(("Unknown category", 7m, 7m), (unknown.Name, unknown.Amount, unknown.DirectAmount));
        Assert.Empty(unknown.Subcategories);
        Assert.Equal(eur.Expenses, eur.ExpenseCategories.Sum(group => group.Amount));
    }

    private CurrencyAnalytics Single(IReadOnlyList<Transaction> transactions, IReadOnlyList<Category>? categories = null) =>
        Assert.Single(MonthlyAnalyticsCalculator.Build(transactions, categories ?? Categories));

    private static Category Expense(string name) =>
        Category.Create(TestUsers.A, name, CategoryType.Expense, parent: null, At);

    private static Category Child(string name, Category parent) =>
        Category.Create(TestUsers.A, name, parent.CategoryType, parent, At);

    private static Transaction Spend(Category category, decimal amount, string currency = "EUR") =>
        SpendOn(category.Id, amount, currency);

    private static Transaction SpendOn(Guid categoryId, decimal amount, string currency = "EUR") =>
        Transaction.CreateExpense(TestUsers.A, Guid.CreateVersion7(), categoryId, amount, currency, At, null, At);

    private Transaction Earn(decimal amount, string currency = "EUR") =>
        Transaction.CreateIncome(TestUsers.A, Guid.CreateVersion7(), _salary.Id, amount, currency, At, null, At);

    private static Transaction Move(decimal amount, string currency = "EUR") =>
        Transaction.CreateTransfer(TestUsers.A, Guid.CreateVersion7(), Guid.CreateVersion7(), amount, currency, At, null, At);
}
