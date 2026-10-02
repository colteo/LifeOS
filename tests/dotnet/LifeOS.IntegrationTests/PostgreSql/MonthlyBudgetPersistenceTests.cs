using LifeOS.Application.Finance.Budgets;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public class MonthlyBudgetPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static MonthlyBudget Budget(Guid owner, decimal amount = 1500, string currency = "EUR") =>
        MonthlyBudget.Create(owner, 2026, 10, currency, amount);

    [Fact]
    public async Task DuplicateKey_IsRejected_DifferentOwnersCurrenciesAndMonthsAreAllowed()
    {
        var a = User.CreateFromExternalIdentity(null, null, Now);
        var b = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, a, b, Budget(a.Id), Budget(b.Id), Budget(a.Id, currency: "USD"),
            Budget(a.Id, currency: "AUD"), MonthlyBudget.Create(a.Id, 2026, 11, "EUR", 10));
        await PostgresAssert.InsertViolatesAsync(fixture, PostgresAssert.UniqueViolation,
            "ux_monthly_budgets_user_month_currency", Budget(a.Id));
    }

    [Fact]
    public async Task AtomicSet_RoundTripsUpdatesAndDeletes_OnlyCallerBudget()
    {
        var a = User.CreateFromExternalIdentity(null, null, Now);
        var b = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, a, b);
        await Set(a.Id, 1500.1234m);
        Guid id;
        await using (var scope = fixture.CreateScope())
        {
            var stored = await Repo(scope).GetAsync(a.Id, 2026, 10, "EUR", default);
            Assert.Equal(1500.1234m, stored!.Amount);
            id = stored.Id;
            Assert.Null(await Repo(scope).GetAsync(b.Id, 2026, 10, "EUR", default));
            await Repo(scope).DeleteAsync(b.Id, 2026, 10, "EUR", default);
        }
        await Set(a.Id, 2000);
        await Set(b.Id, 100);
        await using (var scope = fixture.CreateScope())
        {
            var stored = (await Repo(scope).GetAsync(a.Id, 2026, 10, "EUR", default))!;
            Assert.Equal(id, stored.Id);
            Assert.Equal(2000, stored.Amount);
            await Repo(scope).DeleteAsync(b.Id, 2026, 10, "EUR", default);
            Assert.NotNull(await Repo(scope).GetAsync(a.Id, 2026, 10, "EUR", default));
            await Repo(scope).DeleteAsync(a.Id, 2026, 10, "EUR", default);
            Assert.Null(await Repo(scope).GetAsync(a.Id, 2026, 10, "EUR", default));
        }
    }

    [Fact]
    public async Task ConcurrentFirstSets_PersistExactlyOneBudget()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);
        await Task.WhenAll(Enumerable.Range(1, 8).Select(i => Set(user.Id, i)));
        await using var scope = fixture.CreateScope();
        var stored = Assert.Single(await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().MonthlyBudgets
            .Where(x => x.UserId == user.Id).ToListAsync());
        Assert.InRange(stored.Amount, 1, 8);
    }

    [Fact]
    public async Task SpendingQuery_UsesRealOwnerMonthCurrencyAndExpense_OpeningBalanceExcluded()
    {
        var a = User.CreateFromExternalIdentity(null, null, Now);
        var b = User.CreateFromExternalIdentity(null, null, Now);
        var eur = Account.Create(a.Id, "Cash", AccountType.Cash, "EUR", Now);
        var bank = Account.Create(a.Id, "Bank", AccountType.BankAccount, "EUR", Now);
        var usd = Account.Create(a.Id, "USD", AccountType.Cash, "USD", Now);
        var other = Account.Create(b.Id, "Other", AccountType.Cash, "EUR", Now);
        var expense = Category.Create(a.Id, "Food", CategoryType.Expense, null, Now);
        var income = Category.Create(a.Id, "Salary", CategoryType.Income, null, Now);
        var otherExpense = Category.Create(b.Id, "Food", CategoryType.Expense, null, Now);
        var from = new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero);
        await PostgresAssert.InsertAsync(fixture, a, b, eur, bank, usd, other, expense, income, otherExpense,
            Budget(a.Id), OpeningBalance.Create(eur, 9999, Now, Now),
            Transaction.CreateExpense(a.Id, eur.Id, expense.Id, 600, "EUR", from, null, Now),
            Transaction.CreateExpense(a.Id, eur.Id, expense.Id, 20, "EUR", Now, null, Now),
            Transaction.CreateExpense(a.Id, eur.Id, expense.Id, 999, "EUR", to, null, Now),
            Transaction.CreateExpense(a.Id, eur.Id, expense.Id, 999, "EUR", from.AddTicks(-10), null, Now),
            Transaction.CreateExpense(a.Id, usd.Id, expense.Id, 999, "USD", Now, null, Now),
            Transaction.CreateExpense(b.Id, other.Id, otherExpense.Id, 999, "EUR", Now, null, Now),
            Transaction.CreateIncome(a.Id, eur.Id, income.Id, 999, "EUR", Now, null, Now),
            Transaction.CreateTransfer(a.Id, eur.Id, bank.Id, 1, "EUR", Now, null, Now));

        await using var scope = fixture.CreateScope();
        var get = new GetMonthlyBudgetHandler(Repo(scope), scope.ServiceProvider.GetRequiredService<ITransactionRepository>(), new FixedTimeProvider(Now));
        var result = await get.HandleAsync(a.Id, new(2026, 10, "EUR", from, to, 120), default);
        Assert.Equal((620m, 880m, 40m), (result.Budget!.Spent, result.Budget.Remaining, result.Budget.SafeDailySpend!.Value));
        Assert.Null((await get.HandleAsync(b.Id, new(2026, 10, "EUR", from, to, 120), default)).Budget);
    }

    [Fact]
    public async Task DatabaseChecks_RejectInvalidPlanningData()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user, Budget(user.Id));
        await PostgresAssert.ViolatesAsync("23514", "ck_monthly_budgets_amount_positive", async () =>
        {
            await using var scope = fixture.CreateScope();
            await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE monthly_budgets SET amount = 0 WHERE user_id = {user.Id}");
        });
        await PostgresAssert.InsertViolatesAsync(fixture, PostgresAssert.ForeignKeyViolation,
            "FK_monthly_budgets_users_user_id", Budget(Guid.NewGuid()));
    }

    private async Task Set(Guid owner, decimal amount)
    {
        await using var scope = fixture.CreateScope();
        await Repo(scope).SetAsync(Budget(owner, amount), default);
    }
    private static IMonthlyBudgetRepository Repo(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMonthlyBudgetRepository>();
}
