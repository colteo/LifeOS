using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Budgets;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.PlannedExpenses;
using LifeOS.Application.Finance.Recurring;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.PlannedExpenses;
using LifeOS.Domain.Finance.Recurring;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeOS.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public class PlannedExpensePersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Date = new(2026, 10, 10);
    private static readonly FixedTimeProvider Clock = new(Now);
    private static PlannedExpenseHandler Handler(IServiceProvider p) => new(p.GetRequiredService<IPlannedExpenseRepository>(), Clock);
    private async Task<(User User, Account Account, Category Category, PlannedExpense Item)> Seed(DateOnly? date = null, string currency = "EUR")
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        var account = Account.Create(user.Id, "Checking", AccountType.Cash, currency, Now);
        var category = Category.Create(user.Id, "Fees", CategoryType.Expense, null, Now);
        var item = PlannedExpense.Create(user.Id, "Visa", account, category, 20, date ?? Date, null, Now);
        await PostgresAssert.InsertAsync(fixture, user, account, category, item);
        return (user, account, category, item);
    }

    [Fact]
    public async Task ConcurrentConfirmation_AndRetry_CreateOneNormalExpense_OverridesAndDeletesAreSafe()
    {
        var s = await Seed();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var scope = fixture.CreateScope();
            return await Handler(scope.ServiceProvider).ActAsync(s.User.Id, s.Item.Id, 0, "confirm", new(25, "actual", Now.AddMonths(1)), default);
        }));
        Assert.All(results, r => Assert.Equal(PlannedExpenseResultStatus.Ok, r.Status));
        await using var check = fixture.CreateScope(); var p = check.ServiceProvider; var db = p.GetRequiredService<LifeOSDbContext>(); var h = Handler(p);
        var tx = Assert.Single(await db.Transactions.AsNoTracking().Where(t => t.UserId == s.User.Id).ToListAsync());
        Assert.Equal(TransactionType.Expense, tx.TransactionType); Assert.Equal(25, tx.Amount); Assert.Equal("actual", tx.Note); Assert.Equal(Now.AddMonths(1), tx.OccurredAtUtc);
        Assert.All(results, r => Assert.Equal(tx.Id, r.Transaction!.Id));
        Assert.Single(await db.Set<PlannedExpenseState>().AsNoTracking().Where(i => i.UserId == s.User.Id).ToListAsync());
        Assert.Equal(tx.Id, (await h.ActAsync(s.User.Id, s.Item.Id, 0, "confirm", new(99, null, Now), default)).Transaction!.Id);
        var input = new SavePlannedExpense("Edited", s.Account.Id, s.Category.Id, 10, Date, null);
        Assert.Equal(PlannedExpenseResultStatus.Conflict, (await h.SaveAsync(s.User.Id, s.Item.Id, input, default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.Conflict, (await h.ActAsync(s.User.Id, s.Item.Id, 0, "restore", null, default)).Status);
        Assert.True(await p.GetRequiredService<ITransactionRepository>().DeleteAsync(s.User.Id, tx.Id, default));
        Assert.Equal(PlannedExpenseStatus.Due, (await h.GetAsync(s.User.Id, s.Item.Id, 0, default)).Items.Single().Status);
        Assert.Equal(PlannedExpenseResultStatus.Ok, (await h.SaveAsync(s.User.Id, s.Item.Id, input, default)).Status);
        var again = await h.ActAsync(s.User.Id, s.Item.Id, 0, "confirm", new(10, null, Now), default);
        Assert.Equal(PlannedExpenseResultStatus.Ok, (await h.DeleteAsync(s.User.Id, s.Item.Id, default)).Status);
        Assert.NotNull(await p.GetRequiredService<ITransactionRepository>().GetByIdAsync(s.User.Id, again.Transaction!.Id, default));
        Assert.Empty(await db.Set<PlannedExpenseState>().AsNoTracking().Where(i => i.UserId == s.User.Id).ToListAsync());
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)]
    public async Task CancelRestore_EditAndBoundedQueries_NoAccountingEffect(int days)
    {
        var s = await Seed(Date.AddDays(days)); await using var scope = fixture.CreateScope(); var p = scope.ServiceProvider; var h = Handler(p);
        var expected = days > 0 ? PlannedExpenseStatus.Projected : PlannedExpenseStatus.Due;
        Assert.Equal(expected, (await h.GetAsync(s.User.Id, s.Item.Id, 0, default)).Items.Single().Status);
        Assert.Empty((await h.QueryAsync(s.User.Id, Date.AddMonths(1), Date.AddMonths(2), 0, default)).Items);
        Assert.Equal(PlannedExpenseResultStatus.Invalid, (await h.QueryAsync(s.User.Id, Date, Date.AddDays(-1), 0, default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.Invalid, (await h.QueryAsync(s.User.Id, Date, Date.AddYears(11), 0, default)).Status);
        if (days > 0) Assert.Equal(PlannedExpenseResultStatus.Conflict, (await h.ActAsync(s.User.Id, s.Item.Id, 0, "confirm", new(20, null, Now), default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.Ok, (await h.ActAsync(s.User.Id, s.Item.Id, 0, "cancel", null, default)).Status);
        Assert.Equal(PlannedExpenseStatus.Cancelled, (await h.GetAsync(s.User.Id, s.Item.Id, 0, default)).Items.Single().Status);
        Assert.Equal(PlannedExpenseResultStatus.Conflict, (await h.SaveAsync(s.User.Id, s.Item.Id, new("Edit", s.Account.Id, s.Category.Id, 12, Date, null), default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.Conflict, (await h.ActAsync(s.User.Id, s.Item.Id, 0, "confirm", new(20, null, Now), default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.Ok, (await h.ActAsync(s.User.Id, s.Item.Id, 0, "restore", null, default)).Status);
        Assert.Equal(expected, (await h.GetAsync(s.User.Id, s.Item.Id, 0, default)).Items.Single().Status);
        Assert.Empty(await p.GetRequiredService<LifeOSDbContext>().Transactions.Where(t => t.UserId == s.User.Id).ToListAsync());
        Assert.Equal(PlannedExpenseResultStatus.Ok, (await h.DeleteAsync(s.User.Id, s.Item.Id, default)).Status);
    }

    [Theory]
    [InlineData(20, 10, 70)] [InlineData(25, 10, 65)] [InlineData(25, 11, 90)]
    public async Task Budget_CombinesPlanning_NoDoubleCounting_CrossMonthUsesActualMonth(decimal amount, int month, decimal free)
    {
        var s = await Seed(); var foreign = await Seed();
        var usd = Account.Create(s.User.Id, "USD", AccountType.Cash, "USD", Now);
        var otherCurrency = PlannedExpense.Create(s.User.Id, "USD plan", usd, s.Category, 500, Date, null, Now);
        var future = PlannedExpense.Create(s.User.Id, "Future", s.Account, s.Category, 10, Date.AddDays(10), null, Now);
        var cancelled = PlannedExpense.Create(s.User.Id, "Cancelled", s.Account, s.Category, 700, Date, null, Now);
        var recurring = RecurringTransactionRule.Create(s.User.Id, "Bank fee", TransactionType.Expense, s.Account.Id, s.Category.Id, 10, 1, 2026, 10, null, Now);
        await PostgresAssert.InsertAsync(fixture, usd, otherCurrency, future, cancelled, PlannedExpenseState.Cancel(cancelled, Date, Now), recurring,
            MonthlyBudget.Create(s.User.Id, 2026, 10, "EUR", 110), MonthlyBudget.Create(s.User.Id, 2026, 11, "EUR", 110));
        await using var scope = fixture.CreateScope(); var p = scope.ServiceProvider;
        var budgets = new GetMonthlyBudgetHandler(p.GetRequiredService<IMonthlyBudgetRepository>(), p.GetRequiredService<ITransactionRepository>(), Clock, p.GetRequiredService<IRecurringRepository>(), p.GetRequiredService<IFinancePlanningSnapshotRepository>());
        async Task<MonthlyBudgetSummary> Budget(int m)
        {
            var from = new DateTimeOffset(2026, m, 1, 0, 0, 0, TimeSpan.Zero);
            return (await budgets.HandleAsync(s.User.Id, new(2026, m, "EUR", from, from.AddMonths(1), 0), default)).Budget!;
        }
        var before = await Budget(10); Assert.Equal(30, before.ExpectedPlannedExpenses); Assert.Equal(10, before.ExpectedRecurringExpenses);
        Assert.Equal(40, before.ExpectedExpensesTotal); Assert.Equal(0, before.Spent); Assert.Equal(70, before.FreeToSpend); Assert.Equal(110, before.Remaining);
        var confirmed = await Handler(p).ActAsync(s.User.Id, s.Item.Id, 0, "confirm", new(amount, null, new(2026, month, 10, 12, 0, 0, TimeSpan.Zero)), default);
        Assert.Equal(PlannedExpenseResultStatus.Ok, confirmed.Status);
        var after = await Budget(10); Assert.Equal(10, after.ExpectedPlannedExpenses); Assert.Equal(free, after.FreeToSpend);
        Assert.Equal(free / 22, after.SafeDailySpend); Assert.Equal(month == 10 ? amount : 0, after.Spent);
        Assert.Equal(month == 11 ? amount : 0, (await Budget(11)).Spent);
        Assert.Equal(month == 11 ? 0 : amount, after.Spent);
        Assert.NotEqual(s.User.Id, foreign.User.Id);
    }

    [Fact]
    public async Task Ownership_References_AndDeleteConflicts()
    {
        var a = await Seed(); var b = await Seed(); await using var scope = fixture.CreateScope(); var p = scope.ServiceProvider; var h = Handler(p);
        Assert.Equal(PlannedExpenseResultStatus.NotFound, (await h.GetAsync(b.User.Id, a.Item.Id, 0, default)).Status);
        foreach (var action in new[] { "confirm", "cancel", "restore" })
            Assert.Equal(PlannedExpenseResultStatus.NotFound, (await h.ActAsync(b.User.Id, a.Item.Id, 0, action, new(20, null, Now), default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.NotFound, (await h.DeleteAsync(b.User.Id, a.Item.Id, default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.NotFound, (await h.SaveAsync(b.User.Id, a.Item.Id, new("x", b.Account.Id, b.Category.Id, 1, Date, null), default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.Invalid, (await h.SaveAsync(a.User.Id, null, new("x", b.Account.Id, a.Category.Id, 1, Date, null), default)).Status);
        Assert.Equal(PlannedExpenseResultStatus.Invalid, (await h.SaveAsync(a.User.Id, null, new("x", a.Account.Id, b.Category.Id, 1, Date, null), default)).Status);
        Assert.Equal(AccountDeleteOutcome.HasPlannedExpenses, await p.GetRequiredService<IAccountRepository>().DeleteAsync(a.User.Id, a.Account.Id, default));
        Assert.Equal(CategoryDeleteOutcome.HasPlannedExpenses, await p.GetRequiredService<ICategoryRepository>().DeleteAsync(a.User.Id, a.Category.Id, default));
        await h.ActAsync(a.User.Id, a.Item.Id, 0, "cancel", null, default);
        Assert.Equal(AccountDeleteOutcome.HasPlannedExpenses, await p.GetRequiredService<IAccountRepository>().DeleteAsync(a.User.Id, a.Account.Id, default));
        await h.DeleteAsync(a.User.Id, a.Item.Id, default);
        Assert.Equal(AccountDeleteOutcome.Deleted, await p.GetRequiredService<IAccountRepository>().DeleteAsync(a.User.Id, a.Account.Id, default));
        Assert.Equal(CategoryDeleteOutcome.Deleted, await p.GetRequiredService<ICategoryRepository>().DeleteAsync(a.User.Id, a.Category.Id, default));
    }

    [Theory]
    [InlineData("expected_amount = 0", "ck_planned_expenses_amount")]
    [InlineData("name = ' '", "ck_planned_expenses_name")]
    public async Task Database_Checks(string set, string constraint)
    {
        var s = await Seed(); await using var scope = fixture.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        if (set == "expected_amount = 0")
            await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, constraint, () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE planned_expenses SET expected_amount = 0 WHERE id = {s.Item.Id}"));
        else
            await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, constraint, () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE planned_expenses SET name = ' ' WHERE id = {s.Item.Id}"));
    }

    [Fact]
    public async Task AnalyticsAndBalances_ReadActualsOnly_ThroughEveryPlanningState()
    {
        var s = await Seed();
        var future = PlannedExpense.Create(s.User.Id, "Future", s.Account, s.Category, 500, Date.AddDays(1), null, Now);
        await PostgresAssert.InsertAsync(fixture, future);
        await using var scope = fixture.CreateScope(); var p = scope.ServiceProvider; var h = Handler(p);
        var transactions = p.GetRequiredService<ITransactionRepository>();
        var analytics = new LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics.GetMonthlyAnalyticsHandler(transactions, p.GetRequiredService<ICategoryRepository>());
        var balances = new LifeOS.Application.Finance.Accounts.GetAccountBalances.GetAccountBalancesHandler(
            p.GetRequiredService<IAccountRepository>(), p.GetRequiredService<IOpeningBalanceRepository>(), transactions, Clock,
            p.GetRequiredService<LifeOS.Application.Finance.Accounts.ReconcileAccount.IAccountBalanceAdjustmentRepository>());
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        async Task AssertActuals(decimal expected)
        {
            var a = await analytics.HandleAsync(s.User.Id, new(from, from.AddMonths(1)), default);
            Assert.Equal(expected, a.Currencies.Sum(c => c.Expenses));
            var b = await balances.HandleAsync(s.User.Id, new(Now.AddDays(1)), default);
            Assert.Equal(-expected, b.Balances.Single().Balance);
        }
        await AssertActuals(0);
        await h.ActAsync(s.User.Id, s.Item.Id, 0, "cancel", null, default); await AssertActuals(0);
        await h.ActAsync(s.User.Id, s.Item.Id, 0, "restore", null, default); await AssertActuals(0);
        var actual = await h.ActAsync(s.User.Id, s.Item.Id, 0, "confirm", new(25, null, Now), default); await AssertActuals(25);
        await h.DeleteAsync(s.User.Id, s.Item.Id, default); await AssertActuals(25);
        await transactions.DeleteAsync(s.User.Id, actual.Transaction!.Id, default); await AssertActuals(0);
    }

    [Fact]
    public async Task ConcurrentBudgetReads_NeverMixActualAndExpectedRepresentations()
    {
        var s = await Seed();
        await PostgresAssert.InsertAsync(fixture, MonthlyBudget.Create(s.User.Id, 2026, 10, "EUR", 100));
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var reads = Enumerable.Range(0, 24).Select(async _ =>
        {
            await using var scope = fixture.CreateScope(); var p = scope.ServiceProvider;
            var budgets = new GetMonthlyBudgetHandler(p.GetRequiredService<IMonthlyBudgetRepository>(), p.GetRequiredService<ITransactionRepository>(), Clock,
                p.GetRequiredService<IRecurringRepository>(), p.GetRequiredService<IFinancePlanningSnapshotRepository>());
            return (await budgets.HandleAsync(s.User.Id, new(2026, 10, "EUR", from, from.AddMonths(1), 0), default)).Budget!;
        }).ToList();
        await using var confirm = fixture.CreateScope();
        await Handler(confirm.ServiceProvider).ActAsync(s.User.Id, s.Item.Id, 0, "confirm", new(20, null, Now), default);
        Assert.All(await Task.WhenAll(reads), b =>
        {
            Assert.Equal(80, b.FreeToSpend);
            Assert.True((b.Spent == 0 && b.ExpectedPlannedExpenses == 20) || (b.Spent == 20 && b.ExpectedPlannedExpenses == 0));
        });
    }

    [Fact]
    public async Task Database_Ownership_Shape_Uniqueness_PrecisionAndTransactionLink()
    {
        var a = await Seed(); var b = await Seed(); await using var scope = fixture.CreateScope(); var p = scope.ServiceProvider; var db = p.GetRequiredService<LifeOSDbContext>();
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.ForeignKeyViolation, "fk_planned_expenses_account_owner", () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE planned_expenses SET account_id = {b.Account.Id} WHERE id = {a.Item.Id}"));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.ForeignKeyViolation, "fk_planned_expenses_category_owner", () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE planned_expenses SET category_id = {b.Category.Id} WHERE id = {a.Item.Id}"));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE planned_expenses SET expected_amount = {Transaction.MaxAmount} WHERE id = {a.Item.Id}");
        Assert.Equal(Transaction.MaxAmount, (await Handler(p).GetAsync(a.User.Id, a.Item.Id, 0, default)).Items.Single().ExpectedAmount);
        var confirmed = await Handler(p).ActAsync(a.User.Id, a.Item.Id, 0, "confirm", new(20, null, Now), default);
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_planned_expense_states_shape", () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE planned_expense_states SET transaction_id = NULL WHERE planned_expense_id = {a.Item.Id}"));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_planned_expense_states_shape", () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE planned_expense_states SET status = 'Due' WHERE planned_expense_id = {a.Item.Id}"));
        var foreignTx = Transaction.CreateExpense(b.User.Id, b.Account.Id, b.Category.Id, 20, "EUR", Now, null, Now);
        await PostgresAssert.InsertAsync(fixture, foreignTx);
        // Find the generated FK name from the model; it still enforces owner + Transaction id.
        var fk = db.Model.FindEntityType(typeof(PlannedExpenseState))!.GetForeignKeys().Single(k => k.PrincipalEntityType.ClrType == typeof(Transaction)).GetConstraintName()!;
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.ForeignKeyViolation, fk, () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE planned_expense_states SET transaction_id = {foreignTx.Id} WHERE planned_expense_id = {a.Item.Id}"));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.UniqueViolation, "ux_planned_expense_states_item", () => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO planned_expense_states (id,user_id,planned_expense_id,status,transaction_id,created_at_utc) VALUES ({Guid.NewGuid()},{a.User.Id},{a.Item.Id},'Cancelled',NULL,{Now})"));
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.NotNull(confirmed.Transaction);
    }
}
