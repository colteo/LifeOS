using LifeOS.Application.Finance.Budgets;
using LifeOS.Application.Finance.PlannedExpenses;
using LifeOS.Application.Finance.Recurring;
using LifeOS.App.Services.Finance;
using LifeOS.Contracts.Finance.PlannedExpenses;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.PlannedExpenses;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.PlannedExpenses;

public class PlannedExpenseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 23, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Date = new(2026, 10, 10);
    private static Account Account(Guid? user = null) => LifeOS.Domain.Finance.Accounts.Account.Create(user ?? TestUsers.A, "Cash", AccountType.Cash, "EUR", Now);
    private static Category Category(Guid? user = null, CategoryType type = CategoryType.Expense) => LifeOS.Domain.Finance.Categories.Category.Create(user ?? TestUsers.A, "Fees", type, null, Now);
    private static PlannedExpense Item(decimal amount = 20) => PlannedExpense.Create(TestUsers.A, " Visa ", Account(), Category(), amount, Date, " note ", Now);

    [Theory]
    [InlineData(9, PlannedExpenseStatus.Projected)]
    [InlineData(10, PlannedExpenseStatus.Due)]
    [InlineData(11, PlannedExpenseStatus.Due)]
    public void Status_DerivesOnDemand(int today, PlannedExpenseStatus expected) => Assert.Equal(expected, Item().Status(new(2026, 10, today)));

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1.00001)] [InlineData(1000000000000000)]
    public void Amount_UsesTransactionRangeAndPrecision(decimal amount) => Assert.ThrowsAny<ArgumentException>(() => Item(amount));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CalendarDates_RejectProviderInfinitySentinels(bool maximum) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        PlannedExpense.Create(TestUsers.A, "Visa", Account(), Category(), 20, maximum ? DateOnly.MaxValue : DateOnly.MinValue, null, Now));

    [Fact]
    public void Normalization_Compatibility_AndEditValidation()
    {
        var item = Item(Transaction.MaxAmount); Assert.Equal("Visa", item.Name); Assert.Equal("note", item.Note);
        Assert.Equal(Now, item.CreatedAtUtc); Assert.Equal(Now, item.UpdatedAtUtc);
        Assert.Throws<ArgumentException>(() => PlannedExpense.Create(TestUsers.A, "x", Account(TestUsers.B), Category(), 1, Date, null, Now));
        Assert.Throws<ArgumentException>(() => PlannedExpense.Create(TestUsers.A, "x", Account(), Category(TestUsers.B), 1, Date, null, Now));
        Assert.Throws<ArgumentException>(() => PlannedExpense.Create(TestUsers.A, "x", Account(), Category(type: CategoryType.Income), 1, Date, null, Now));
        Assert.Throws<ArgumentException>(() => PlannedExpense.Create(Guid.Empty, "x", Account(), Category(), 1, Date, null, Now));
        Assert.Throws<ArgumentException>(() => PlannedExpense.Create(TestUsers.A, " ", Account(), Category(), 1, Date, null, Now));
        var account = Account(); var category = Category();
        item.Update(" New ", account, category, 12.3456m, Date.AddDays(2), " ", Now.AddDays(1));
        Assert.Equal("New", item.Name); Assert.Null(item.Note); Assert.Equal(account.Id, item.AccountId);
        Assert.Equal(12.3456m, item.ExpectedAmount); Assert.Equal(Date.AddDays(2), item.ScheduledDate);
        Assert.ThrowsAny<ArgumentException>(() => item.Update("Bad", account, category, 0, Date, null, Now));
        Assert.Equal("New", item.Name); // Invalid updates are not partial.
    }

    [Fact]
    public void Processing_IsExplicit_Immutable_AndOwnershipSafe()
    {
        var item = Item(); var cancelled = PlannedExpenseState.Cancel(item, Date, Now);
        Assert.Equal(PlannedExpenseStatus.Cancelled, item.Status(Date, cancelled)); Assert.Null(cancelled.TransactionId);
        Assert.Equal(PlannedExpenseStatus.Projected, item.Status(Date.AddDays(-1))); // Restore removes state.
        Assert.Throws<InvalidOperationException>(() => item.Update("New", Account(), Category(), 1, Date, null, Now, cancelled));
        var tx = Transaction.CreateExpense(item.UserId, item.AccountId, item.CategoryId, 25, "EUR", Now.AddMonths(1), "actual", Now);
        var confirmed = PlannedExpenseState.Confirm(item, Date, tx, Now);
        Assert.Equal(tx.Id, confirmed.TransactionId); Assert.Equal(PlannedExpenseStatus.Confirmed, item.Status(Date.AddDays(-1), confirmed));
        Assert.Throws<InvalidOperationException>(() => item.Update("New", Account(), Category(), 1, Date, null, Now, confirmed));
        Assert.Throws<InvalidOperationException>(() => PlannedExpenseState.Confirm(item, Date.AddDays(-1), tx, Now));
        Assert.Throws<ArgumentException>(() => Item().Status(Date, confirmed));
        var wrong = Transaction.CreateExpense(TestUsers.B, item.AccountId, item.CategoryId, 25, "EUR", Now, null, Now);
        Assert.Throws<ArgumentException>(() => PlannedExpenseState.Confirm(item, Date, wrong, Now));
        Assert.Equal(20, item.ExpectedAmount); Assert.Equal(Date, item.ScheduledDate); // Actual overrides never rewrite planning.
    }

    [Fact]
    public void Today_UsesValidatedOffset_AcrossUtcMidnight()
    {
        Assert.Equal(Date.AddDays(1), RecurringHandler.LocalToday(new FixedTimeProvider(Now), 120));
        Assert.Equal(Date, RecurringHandler.LocalToday(new FixedTimeProvider(Now), -120));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecurringHandler.LocalToday(new FixedTimeProvider(Now), 841));
    }

    [Theory]
    [InlineData(20, 0)] [InlineData(25, -5)]
    public void Budget_AggregatesExpected_AndConfirmationDelta(decimal actual, decimal delta)
    {
        var budget = MonthlyBudget.Create(TestUsers.A, 2026, 10, "EUR", 100);
        var before = MonthlyBudgetCalculator.Build(budget, [], Now.AddDays(-10), Now.AddDays(20), Date, 10, 20);
        Assert.Equal(100, before.Remaining); Assert.Equal(30, before.ExpectedExpensesTotal); Assert.Equal(70, before.FreeToSpend);
        Assert.Equal(70m / 22, before.SafeDailySpend);
        var tx = Transaction.CreateExpense(TestUsers.A, Guid.NewGuid(), Guid.NewGuid(), actual, "EUR", Now, null, Now);
        var after = MonthlyBudgetCalculator.Build(budget, [tx, tx], Now.AddDays(-10), Now.AddDays(20), Date, 10, 0);
        Assert.Equal(70 + delta, after.FreeToSpend); Assert.Equal(actual, after.Spent);
    }

    [Theory]
    [InlineData("Projected", false, true)] [InlineData("Due", true, true)]
    [InlineData("Confirmed", false, false)] [InlineData("Cancelled", false, false)]
    public void App_StatusActions_AndMonthFilter(string status, bool confirm, bool cancel)
    {
        var i = Response(status); var flow = new PlannedExpenseFlow(i);
        Assert.Equal(confirm, flow.CanConfirm); Assert.Equal(cancel, flow.CanCancel);
        Assert.Equal(cancel ? 1 : 0, PlannedExpensePlanning.ForMonth([i], 2026, 10).Count);
        Assert.Empty(PlannedExpensePlanning.ForMonth([i], 2026, 11));
    }

    [Theory]
    [InlineData("2026-10-10T12:00")] [InlineData("2026-10-10T12:00:00")] [InlineData("2026-10-10T12:00:00.123")]
    public void Review_DefaultsAndOverride_UsesEnteredDatesZone(string local)
    {
        var flow = new PlannedExpenseFlow(Response("Due")); flow.Review();
        Assert.Equal("20", flow.Amount); Assert.Equal("2026-10-10T12:00", flow.LocalDateTime);
        flow.Amount = "25"; flow.Note = "actual"; flow.LocalDateTime = local;
        Assert.True(flow.TryConfirmation(TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome"), out var request));
        Assert.Equal(25, request!.Amount); Assert.Equal(10, request.OccurredAtUtc.Hour); Assert.Equal("actual", request.Note);
        flow.Amount = "0"; Assert.False(flow.TryConfirmation(TimeZoneInfo.Utc, out _)); Assert.NotNull(flow.Error);
        flow.Amount = "1"; flow.LocalDateTime = "bad"; Assert.False(flow.TryConfirmation(TimeZoneInfo.Utc, out _));
    }

    private static PlannedExpenseResponse Response(string status) => new(Guid.NewGuid(), "Visa", Guid.NewGuid(), Guid.NewGuid(), "EUR", 20, Date, "note", status, null, Now, Now);

    [Fact]
    public async Task Client_CalendarQuery_UsesInvariantGregorianDates()
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("th-TH");
            using var http = new HttpClient(new QueryHandler()) { BaseAddress = new("https://example.test/") };
            Assert.True((await new PlannedExpensesApiClient(http).QueryAsync(new(2026, 10, 1), new(2026, 10, 1))).IsSuccess);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }
    private sealed class QueryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Contains("from=2026-10-01&to=2026-10-31", request.RequestUri!.Query);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
