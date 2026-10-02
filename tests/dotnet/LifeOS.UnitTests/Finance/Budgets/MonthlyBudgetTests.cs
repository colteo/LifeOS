using LifeOS.Application.Finance.Budgets;
using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Budgets;

public class MonthlyBudgetTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddMonths(1);
    private static readonly Guid Account = Guid.NewGuid(), Category = Guid.NewGuid();

    [Theory]
    [InlineData(0, 1500, 1500)]
    [InlineData(620, 880, 880)]
    [InlineData(1500, 0, 0)]
    [InlineData(1600, -100, 0)]
    public void Spending_ControlsRemainingAndSafeSpend(int spent, int remaining, int numerator)
    {
        var transactions = spent == 0 ? Array.Empty<Transaction>() : [Expense(TestUsers.A, spent)];
        var result = MonthlyBudgetCalculator.Build(Budget(), transactions, From, To, new(2026, 10, 10));
        Assert.Equal(spent, result.Spent);
        Assert.Equal(remaining, result.Remaining);
        Assert.Equal(22, result.RemainingDays);
        Assert.Equal(numerator / 22m, result.SafeDailySpend);
    }

    [Theory]
    [InlineData(1, 31)]
    [InlineData(10, 22)]
    [InlineData(31, 1)]
    public void RemainingDays_IncludeToday(int day, int days)
    {
        var result = MonthlyBudgetCalculator.Build(Budget(), [], From, To, new(2026, 10, day));
        Assert.Equal(days, result.RemainingDays);
        Assert.Equal(1500m / days, result.SafeDailySpend);
    }

    [Fact]
    public void LeapMonth_HasCorrectDayCount()
    {
        var budget = MonthlyBudget.Create(TestUsers.A, 2028, 2, "EUR", 290);
        var from = new DateTimeOffset(2028, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var result = MonthlyBudgetCalculator.Build(budget, [], from, from.AddMonths(1), new(2028, 2, 1));
        Assert.Equal(29, result.RemainingDays);
        Assert.Equal(10, result.SafeDailySpend);
    }

    [Fact]
    public void OnlyUniqueExpensesInMonthCurrencyAndOwner_AreCounted()
    {
        var included = Expense(TestUsers.A, 10, occurred: From);
        Transaction[] transactions = [included, included, Expense(TestUsers.B, 999), Expense(TestUsers.A, 999, "USD"),
            Expense(TestUsers.A, 999, "AUD"), Expense(TestUsers.A, 999, occurred: From.AddTicks(-1)),
            Expense(TestUsers.A, 999, occurred: To),
            Transaction.CreateIncome(TestUsers.A, Account, Category, 999, "EUR", From, null, From),
            Transaction.CreateTransfer(TestUsers.A, Account, Guid.NewGuid(), 999, "EUR", From, null, From)];
        var result = MonthlyBudgetCalculator.Build(Budget(), transactions, From, To, new(2026, 10, 31));
        Assert.Equal(10, result.Spent);
        Assert.Equal(1490, result.SafeDailySpend);
    }

    [Fact]
    public void OtherMonth_DoesNotHaveSafeDailySpend()
    {
        var result = MonthlyBudgetCalculator.Build(Budget(), [], From, To, new(2026, 11, 1));
        Assert.Null(result.SafeDailySpend);
        Assert.Null(result.RemainingDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.00001)]
    public void InvalidAmount_IsRejected(decimal amount) =>
        Assert.ThrowsAny<ArgumentException>(() => MonthlyBudget.Create(TestUsers.A, 2026, 10, "EUR", amount));

    [Theory]
    [InlineData(0, 10, "EUR")]
    [InlineData(9999, 10, "EUR")]
    [InlineData(2026, 0, "EUR")]
    [InlineData(2026, 13, "EUR")]
    [InlineData(2026, 10, "EU")]
    public void InvalidKey_IsRejected(int year, int month, string currency) =>
        Assert.ThrowsAny<ArgumentException>(() => MonthlyBudget.Create(TestUsers.A, year, month, currency, 1));

    [Fact]
    public async Task Handlers_MissingSetUpdateDeleteAndOwnership()
    {
        var budgets = new InMemoryMonthlyBudgetRepository();
        var transactions = new InMemoryTransactionRepository();
        var get = new GetMonthlyBudgetHandler(budgets, transactions, new FixedTimeProvider(From.AddDays(9)));
        var query = new GetMonthlyBudgetQuery(2026, 10, "eur", From, To, 0);
        Assert.Null((await get.HandleAsync(TestUsers.A, query, default)).Budget);
        var set = new SetMonthlyBudgetHandler(budgets);
        Assert.Equal(MonthlyBudgetStatus.Ok, (await set.HandleAsync(TestUsers.A, new(2026, 10, " eur ", 1500), default)).Status);
        Assert.Null((await get.HandleAsync(TestUsers.B, query, default)).Budget);
        transactions.Transactions.AddRange([Expense(TestUsers.A, 620), Expense(TestUsers.B, 999)]);
        Assert.Equal(40, (await get.HandleAsync(TestUsers.A, query, default)).Budget!.SafeDailySpend);
        await set.HandleAsync(TestUsers.B, new(2026, 10, "EUR", 100), default);
        await set.HandleAsync(TestUsers.A, new(2026, 10, "EUR", 1600), default);
        var delete = new DeleteMonthlyBudgetHandler(budgets);
        await delete.HandleAsync(TestUsers.B, 2026, 10, "EUR", default);
        Assert.Equal(1600, (await get.HandleAsync(TestUsers.A, query, default)).Budget!.Amount);
        await delete.HandleAsync(TestUsers.A, 2026, 10, "EUR", default);
        Assert.Null((await get.HandleAsync(TestUsers.A, query, default)).Budget);
        Assert.Equal(2, transactions.Transactions.Count);
    }

    [Fact]
    public async Task CurrentDate_UsesDeviceOffsetAndServerClock()
    {
        var budgets = new InMemoryMonthlyBudgetRepository();
        await budgets.SetAsync(Budget(), default);
        var get = new GetMonthlyBudgetHandler(budgets, new InMemoryTransactionRepository(), new FixedTimeProvider(From.AddHours(-1)));
        var result = await get.HandleAsync(TestUsers.A, new(2026, 10, "EUR", From.AddHours(-2), To.AddHours(-1), 120), default);
        Assert.Equal(31, result.Budget!.RemainingDays);
    }

    [Theory]
    [InlineData(841, 0, 31)]
    [InlineData(0, 1, 31)]
    [InlineData(0, 0, 60)]
    public async Task InvalidReadRangeOrOffset_ReturnsInvalid(int offset, int startDays, int endDays)
    {
        var get = new GetMonthlyBudgetHandler(new InMemoryMonthlyBudgetRepository(), new InMemoryTransactionRepository(), new FixedTimeProvider(From));
        Assert.Equal(MonthlyBudgetStatus.Invalid,
            (await get.HandleAsync(TestUsers.A, new(2026, 10, "EUR", From.AddDays(startDays), From.AddDays(endDays), offset), default)).Status);
    }

    private static MonthlyBudget Budget() => MonthlyBudget.Create(TestUsers.A, 2026, 10, "EUR", 1500);
    private static Transaction Expense(Guid userId, decimal amount, string currency = "EUR", DateTimeOffset? occurred = null) =>
        Transaction.CreateExpense(userId, Account, Category, amount, currency, occurred ?? From.AddDays(2), null, From);
}
