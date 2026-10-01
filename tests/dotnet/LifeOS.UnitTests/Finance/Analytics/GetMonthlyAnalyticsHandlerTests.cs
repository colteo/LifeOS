using LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Analytics;

public class GetMonthlyAnalyticsHandlerTests
{
    // September 2026 in Europe/Rome (UTC+2): local 2026-09-01 00:00 → 2026-10-01 00:00.
    private static readonly DateTimeOffset FromUtc = new(2026, 8, 31, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ToUtc = new(2026, 9, 30, 22, 0, 0, TimeSpan.Zero);

    private readonly InMemoryTransactionRepository _transactions = new();
    private readonly InMemoryCategoryRepository _categories = new();
    private readonly Category _travel;
    private readonly Category _travelOfB;

    public GetMonthlyAnalyticsHandlerTests()
    {
        _travel = AddCategory(TestUsers.A, "Travel");
        _travelOfB = AddCategory(TestUsers.B, "Travel");
    }

    [Fact]
    public async Task FromIsInclusive_ToIsExclusive()
    {
        AddExpense(TestUsers.A, _travel, 1m, FromUtc);
        AddExpense(TestUsers.A, _travel, 10m, ToUtc.AddTicks(-10));
        AddExpense(TestUsers.A, _travel, 100m, ToUtc);
        AddExpense(TestUsers.A, _travel, 1000m, FromUtc.AddTicks(-10));

        var result = await HandleAsync(TestUsers.A, FromUtc, ToUtc);

        Assert.Equal(GetMonthlyAnalyticsStatus.Ok, result.Status);
        Assert.Equal(11m, Assert.Single(result.Currencies).Expenses);
    }

    [Fact]
    public async Task LocalMonthBoundaries_KeepLateEveningAndEarlyMorningInTheRightMonth()
    {
        // 31 August 23:30 local (21:30Z) belongs to August; 30 September 23:30 local (21:30Z) to September.
        AddExpense(TestUsers.A, _travel, 5m, new DateTimeOffset(2026, 8, 31, 21, 30, 0, TimeSpan.Zero));
        AddExpense(TestUsers.A, _travel, 7m, new DateTimeOffset(2026, 9, 30, 21, 30, 0, TimeSpan.Zero));

        var result = await HandleAsync(TestUsers.A, FromUtc, ToUtc);

        Assert.Equal(7m, Assert.Single(result.Currencies).Expenses);
    }

    [Fact]
    public async Task AnotherUsersTransactionsAndCategories_HaveNoEffect()
    {
        AddExpense(TestUsers.A, _travel, 10m, FromUtc.AddDays(1));
        AddExpense(TestUsers.B, _travelOfB, 500m, FromUtc.AddDays(1));
        AddExpense(TestUsers.B, _travelOfB, 9m, FromUtc.AddDays(1), "USD");

        var result = await HandleAsync(TestUsers.A, FromUtc, ToUtc);

        var eur = Assert.Single(result.Currencies);
        Assert.Equal(10m, eur.Expenses);
        Assert.Equal(_travel.Id, Assert.Single(eur.ExpenseCategories).CategoryId);
    }

    [Fact]
    public async Task NoActivity_GivesNoCurrencyBlocks()
    {
        var result = await HandleAsync(TestUsers.A, FromUtc, ToUtc);

        Assert.Equal(GetMonthlyAnalyticsStatus.Ok, result.Status);
        Assert.Empty(result.Currencies);
    }

    [Fact]
    public async Task ExactlyThirtyTwoDays_IsAccepted()
    {
        var result = await HandleAsync(TestUsers.A, FromUtc, FromUtc.AddDays(32));

        Assert.Equal(GetMonthlyAnalyticsStatus.Ok, result.Status);
    }

    [Theory]
    [InlineData("offset-from")]
    [InlineData("offset-to")]
    [InlineData("equal")]
    [InlineData("reversed")]
    [InlineData("too-long")]
    public async Task InvalidRanges_AreRejected(string variant)
    {
        var (from, to, field) = variant switch
        {
            "offset-from" => (FromUtc.ToOffset(TimeSpan.FromHours(2)), ToUtc, "fromUtc"),
            "offset-to" => (FromUtc, ToUtc.ToOffset(TimeSpan.FromHours(2)), "toUtc"),
            "equal" => (FromUtc, FromUtc, "toUtc"),
            "reversed" => (ToUtc, FromUtc, "toUtc"),
            _ => (FromUtc, FromUtc.AddDays(32).AddTicks(1), "toUtc")
        };

        var result = await HandleAsync(TestUsers.A, from, to);

        Assert.Equal(GetMonthlyAnalyticsStatus.Invalid, result.Status);
        Assert.Equal(field, result.Field);
        Assert.Empty(result.Currencies);
    }

    private Task<GetMonthlyAnalyticsResult> HandleAsync(Guid userId, DateTimeOffset from, DateTimeOffset to) =>
        new GetMonthlyAnalyticsHandler(_transactions, _categories)
            .HandleAsync(userId, new GetMonthlyAnalyticsQuery(from, to), CancellationToken.None);

    private Category AddCategory(Guid userId, string name)
    {
        var category = Category.Create(userId, name, CategoryType.Expense, parent: null, FromUtc);
        _categories.Categories.Add(category);

        return category;
    }

    private void AddExpense(Guid userId, Category category, decimal amount, DateTimeOffset occurredAtUtc, string currency = "EUR") =>
        _transactions.Transactions.Add(
            Transaction.CreateExpense(userId, Guid.CreateVersion7(), category.Id, amount, currency, occurredAtUtc, null, occurredAtUtc));
}
