using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Budgets;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Recurring;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.Finance.Categories;
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
public class RecurringPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly FixedTimeProvider Clock = new(Now);
    private RecurringHandler Handler(IServiceProvider services) => new(services.GetRequiredService<IRecurringRepository>(), Clock);

    private async Task<(User User, Account Account, Category Category, RecurringTransactionRule Rule)> Seed(decimal amount = 20, int day = 1)
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        var account = Account.Create(user.Id, "Checking", AccountType.Cash, "EUR", Now);
        var category = Category.Create(user.Id, "Fee", CategoryType.Expense, null, Now);
        var rule = RecurringTransactionRule.Create(user.Id, "Fee", TransactionType.Expense, account.Id, category.Id, amount, day, 2026, 2, null, Now);
        await PostgresAssert.InsertAsync(fixture, user, account, category, rule);
        return (user, account, category, rule);
    }

    [Fact]
    public async Task ConcurrentConfirmation_AndRetry_CreateExactlyOneActualTransaction()
    {
        var s = await Seed();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var scope = fixture.CreateScope();
            return await Handler(scope.ServiceProvider).ActAsync(s.User.Id, s.Rule.Id, 2026, 10, 0, "confirm", new(25, "actual", Now.AddDays(-1)), default);
        }));
        Assert.All(results, r => Assert.Equal(RecurringResultStatus.Ok, r.Status));
        await using var check = fixture.CreateScope(); var db = check.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        var actual = Assert.Single(await db.Transactions.Where(t => t.UserId == s.User.Id).ToListAsync());
        Assert.Equal(25, actual.Amount); Assert.Equal("actual", actual.Note);
        Assert.All(results, r => Assert.Equal(actual.Id, r.Transaction!.Id));
        Assert.Single(await db.Set<RecurringOccurrenceState>().Where(o => o.UserId == s.User.Id).ToListAsync());
        var rule = await db.Set<RecurringTransactionRule>().SingleAsync(r => r.Id == s.Rule.Id);
        Assert.Equal(20, rule.Amount);
    }

    [Fact]
    public async Task Deletion_ReopensMonth_RuleDeletionPreservesActualHistory_AndReferencesRestrict()
    {
        var s = await Seed();
        await using var scope = fixture.CreateScope(); var services = scope.ServiceProvider;
        var h = Handler(services); var db = services.GetRequiredService<LifeOSDbContext>();
        Assert.Equal(AccountDeleteOutcome.HasRecurringRules, await services.GetRequiredService<IAccountRepository>().DeleteAsync(s.User.Id, s.Account.Id, default));
        Assert.Equal(CategoryDeleteOutcome.HasRecurringRules, await services.GetRequiredService<ICategoryRepository>().DeleteAsync(s.User.Id, s.Category.Id, default));
        var result = await h.ActAsync(s.User.Id, s.Rule.Id, 2026, 10, 0, "confirm", new(20, null, Now.AddDays(-1)), default);
        var transactions = services.GetRequiredService<ITransactionRepository>();
        var actual = result.Transaction!;
        actual.UpdateAccountTransaction(s.Account.Id, s.Category.Id, 30, "EUR", Now.AddDays(-1), "edited");
        Assert.Equal(TransactionUpdateOutcome.Updated, await transactions.TryUpdateAsync(actual, default));
        Assert.Equal(OccurrenceStatus.Confirmed, (await h.QueryAsync(s.User.Id, 2026, 10, 2026, 10, 0, default)).Occurrences.Single().Status);
        Assert.True(await transactions.DeleteAsync(s.User.Id, actual.Id, default));
        Assert.Empty(await db.Set<RecurringOccurrenceState>().Where(o => o.UserId == s.User.Id).ToListAsync());
        Assert.Equal(OccurrenceStatus.Due, (await h.QueryAsync(s.User.Id, 2026, 10, 2026, 10, 0, default)).Occurrences.Single().Status);
        var second = await h.ActAsync(s.User.Id, s.Rule.Id, 2026, 10, 0, "confirm", new(20, null, Now.AddDays(-1)), default);
        Assert.NotEqual(actual.Id, second.Transaction!.Id);
        await h.DeleteAsync(s.User.Id, s.Rule.Id, default);
        Assert.NotNull(await transactions.GetByIdAsync(s.User.Id, second.Transaction.Id, default));
        Assert.Empty(await db.Set<RecurringOccurrenceState>().Where(o => o.UserId == s.User.Id).ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData(20, 800)] [InlineData(25, 795)]
    public async Task Budget_Exclusions_Transitions_AndActualOnlyBalances(decimal amount, decimal free)
    {
        var s = await Seed();
        var other = await Seed(999);
        var dollars = Account.Create(s.User.Id, "USD", AccountType.Cash, "USD", Now);
        var income = Category.Create(s.User.Id, "Salary", CategoryType.Income, null, Now);
        var future = RecurringTransactionRule.Create(s.User.Id, "Future", TransactionType.Expense, s.Account.Id, s.Category.Id, 60, 31, 2026, 10, null, Now);
        var salary = RecurringTransactionRule.Create(s.User.Id, "Salary", TransactionType.Income, s.Account.Id, income.Id, 1000, 1, 2026, 10, null, Now);
        var usd = RecurringTransactionRule.Create(s.User.Id, "USD", TransactionType.Expense, dollars.Id, s.Category.Id, 999, 1, 2026, 10, null, Now);
        await PostgresAssert.InsertAsync(fixture, dollars, income, future, salary, usd,
            MonthlyBudget.Create(s.User.Id, 2026, 10, "EUR", 1500),
            Transaction.CreateExpense(s.User.Id, s.Account.Id, s.Category.Id, 620, "EUR", Now.AddDays(-1), null, Now));
        await using var scope = fixture.CreateScope(); var p = scope.ServiceProvider; var h = Handler(p);
        var budgets = new GetMonthlyBudgetHandler(p.GetRequiredService<IMonthlyBudgetRepository>(), p.GetRequiredService<ITransactionRepository>(), Clock, p.GetRequiredService<IRecurringRepository>());
        async Task<MonthlyBudgetSummary> Read() => (await budgets.HandleAsync(s.User.Id, new(2026, 10, "EUR", From, From.AddMonths(1), 0), default)).Budget!;
        var b = await Read(); Assert.Equal((620m, 80m, 800m, 880m), (b.Spent, b.ExpectedRecurringExpenses, b.FreeToSpend, b.Remaining));
        await h.ActAsync(s.User.Id, future.Id, 2026, 10, 0, "skip", null, default);
        Assert.Equal(20, (await Read()).ExpectedRecurringExpenses);
        await h.ActAsync(s.User.Id, future.Id, 2026, 10, 0, "restore", null, default);
        Assert.Equal(80, (await Read()).ExpectedRecurringExpenses);
        Assert.Equal(-620, AccountBalanceCalculator.Calculate(s.Account, null, await p.GetRequiredService<ITransactionRepository>().GetOccurredBeforeAsync(s.User.Id, Now, default), Now));
        await h.ActAsync(s.User.Id, s.Rule.Id, 2026, 10, 0, "confirm", new(amount, null, Now.AddDays(-1)), default);
        b = await Read(); Assert.Equal(620 + amount, b.Spent); Assert.Equal(60, b.ExpectedRecurringExpenses);
        Assert.Equal(free, b.FreeToSpend); Assert.Equal(free / 22, b.SafeDailySpend);
        var movements = await p.GetRequiredService<ITransactionRepository>().GetOccurredBeforeAsync(s.User.Id, Now, default);
        Assert.Equal(2, movements.Count); Assert.Equal(-620 - amount, AccountBalanceCalculator.Calculate(s.Account, null, movements, Now));
        var analytics = await new LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics.GetMonthlyAnalyticsHandler(
            p.GetRequiredService<ITransactionRepository>(), p.GetRequiredService<ICategoryRepository>()).HandleAsync(s.User.Id, new(From, From.AddMonths(1)), default);
        Assert.Equal(620 + amount, analytics.Currencies.Single().Expenses);
    }

    [Fact]
    public async Task Database_EnforcesOwnership_UniqueMonth_Shape_Precision_AndDeleteRestrictions()
    {
        var s = await Seed(20.1234m); var b = await Seed();
        var foreign = RecurringTransactionRule.Create(s.User.Id, "Foreign", TransactionType.Expense, b.Account.Id, s.Category.Id, 1, 1, 2026, 10, null, Now);
        await PostgresAssert.InsertViolatesAsync(fixture, PostgresErrorCodes.ForeignKeyViolation, "fk_recurring_rules_account_owner", foreign);
        foreign = RecurringTransactionRule.Create(s.User.Id, "Foreign", TransactionType.Expense, s.Account.Id, b.Category.Id, 1, 1, 2026, 10, null, Now);
        await PostgresAssert.InsertViolatesAsync(fixture, PostgresErrorCodes.ForeignKeyViolation, "fk_recurring_rules_category_owner", foreign);
        var skipped = RecurringOccurrenceState.Create(s.Rule, 2026, 10, OccurrenceStatus.Skipped, null, Now);
        await PostgresAssert.InsertAsync(fixture, skipped);
        await PostgresAssert.InsertViolatesAsync(fixture, PostgresErrorCodes.UniqueViolation, "ux_recurring_occurrences_rule_month",
            RecurringOccurrenceState.Create(s.Rule, 2026, 10, OccurrenceStatus.Skipped, null, Now));
        var losingTransaction = Transaction.CreateExpense(s.User.Id, s.Account.Id, s.Category.Id, 1, "EUR", Now, null, Now);
        await PostgresAssert.InsertViolatesAsync(fixture, PostgresErrorCodes.UniqueViolation, "ux_recurring_occurrences_rule_month",
            losingTransaction, RecurringOccurrenceState.Create(s.Rule, 2026, 10, OccurrenceStatus.Confirmed, losingTransaction.Id, Now));
        var actual = Transaction.CreateExpense(b.User.Id, b.Account.Id, b.Category.Id, 1, "EUR", Now, null, Now);
        await PostgresAssert.InsertAsync(fixture, actual);
        await PostgresAssert.InsertViolatesAsync(fixture, PostgresErrorCodes.ForeignKeyViolation,
            "FK_recurring_transaction_occurrences_transactions_transaction_~",
            RecurringOccurrenceState.Create(s.Rule, 2026, 11, OccurrenceStatus.Confirmed, actual.Id, Now));
        await using var scope = fixture.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        Assert.False(await db.Transactions.AnyAsync(t => t.Id == losingTransaction.Id));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.ForeignKeyViolation,
            "FK_recurring_transaction_occurrences_recurring_transaction_rul~", () =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE recurring_transaction_occurrences SET user_id = {b.User.Id} WHERE id = {skipped.Id}"));
        Assert.Equal(20.1234m, (await db.Set<RecurringTransactionRule>().SingleAsync(r => r.Id == s.Rule.Id)).Amount);
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_recurring_occurrences_shape", () =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE recurring_transaction_occurrences SET status = 'Due' WHERE id = {skipped.Id}"));
        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, "ck_recurring_rules_day", () =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE recurring_transaction_rules SET day_of_month = 32 WHERE id = {s.Rule.Id}"));
        await PostgresAssert.DeleteBlockedAsync("fk_recurring_rules_account_owner", () => db.Accounts.Where(a => a.Id == s.Account.Id).ExecuteDeleteAsync());
        await PostgresAssert.DeleteBlockedAsync("fk_recurring_rules_category_owner", () => db.Categories.Where(c => c.Id == s.Category.Id).ExecuteDeleteAsync());
        await Handler(scope.ServiceProvider).DeleteAsync(s.User.Id, s.Rule.Id, default);
        Assert.Equal(CategoryDeleteOutcome.Deleted, await scope.ServiceProvider.GetRequiredService<ICategoryRepository>().DeleteAsync(s.User.Id, s.Category.Id, default));
        Assert.Equal(AccountDeleteOutcome.Deleted, await scope.ServiceProvider.GetRequiredService<IAccountRepository>().DeleteAsync(s.User.Id, s.Account.Id, default));
    }

    [Fact]
    public async Task ConcurrentBudgetReads_NeverMixConfirmationRepresentations()
    {
        var s = await Seed();
        await PostgresAssert.InsertAsync(fixture, MonthlyBudget.Create(s.User.Id, 2026, 10, "EUR", 100));
        var reads = Enumerable.Range(0, 24).Select(async _ =>
        {
            await using var scope = fixture.CreateScope(); var p = scope.ServiceProvider;
            var handler = new GetMonthlyBudgetHandler(p.GetRequiredService<IMonthlyBudgetRepository>(),
                p.GetRequiredService<ITransactionRepository>(), Clock, p.GetRequiredService<IRecurringRepository>());
            return (await handler.HandleAsync(s.User.Id, new(2026, 10, "EUR", From, From.AddMonths(1), 0), default)).Budget!;
        }).ToList();
        await using (var scope = fixture.CreateScope())
            Assert.Equal(RecurringResultStatus.Ok, (await Handler(scope.ServiceProvider).ActAsync(s.User.Id, s.Rule.Id,
                2026, 10, 0, "confirm", new(20, null, Now.AddDays(-1)), default)).Status);
        foreach (var read in await Task.WhenAll(reads)) Assert.Equal(80, read.FreeToSpend);
    }
}
