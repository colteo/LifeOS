using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public class FinanceOwnershipPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OccurredAtUtc = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    // ---- Test 5: repositories scope every read to the user ----

    [Fact]
    public async Task AccountRepository_ReturnsOnlyTheUsersAccounts()
    {
        var (a, b) = await NewUsersAsync();
        var accountA = NewAccount(a);
        var accountB = NewAccount(b);
        await PostgresAssert.InsertAsync(fixture, accountA, accountB);

        await using var scope = fixture.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();

        Assert.Equal(accountA.Id, Assert.Single(await accounts.GetAllAsync(a.Id, CancellationToken.None)).Id);
        Assert.Null(await accounts.GetByIdAsync(b.Id, accountA.Id, CancellationToken.None));
        Assert.NotNull(await accounts.GetByIdAsync(a.Id, accountA.Id, CancellationToken.None));
        Assert.True(await accounts.AnyAsync(a.Id, CancellationToken.None));
        Assert.False(await accounts.AnyAsync(Guid.CreateVersion7(), CancellationToken.None));
    }

    [Fact]
    public async Task CategoryRepository_ReturnsOnlyTheUsersCategories()
    {
        var (a, b) = await NewUsersAsync();
        var casaA = TopLevel(a, "Casa");
        var casaB = TopLevel(b, "Casa");
        await PostgresAssert.InsertAsync(fixture, casaA, casaB);

        await using var scope = fixture.CreateScope();
        var categories = scope.ServiceProvider.GetRequiredService<ICategoryRepository>();

        Assert.Equal(casaA.Id, Assert.Single(await categories.GetAllAsync(a.Id, CancellationToken.None)).Id);
        Assert.Null(await categories.GetByIdAsync(b.Id, casaA.Id, CancellationToken.None));

        // Sibling lookups used by the duplicate-name check are per user.
        var siblingsOfA = await categories.GetByTypeAndParentAsync(a.Id, CategoryType.Expense, null, CancellationToken.None);
        Assert.Equal(casaA.Id, Assert.Single(siblingsOfA).Id);
    }

    [Fact]
    public async Task TransactionRepository_ReturnsOnlyTheUsersTransactions()
    {
        var (a, b) = await NewUsersAsync();
        var accountA = NewAccount(a);
        var categoryA = TopLevel(a, "Casa");
        var accountB = NewAccount(b);
        var categoryB = TopLevel(b, "Casa");
        var expenseA = Transaction.CreateExpense(a.Id, accountA.Id, categoryA.Id, 10m, "EUR", OccurredAtUtc, null, Now);
        var expenseB = Transaction.CreateExpense(b.Id, accountB.Id, categoryB.Id, 20m, "EUR", OccurredAtUtc, null, Now);
        await PostgresAssert.InsertAsync(fixture, accountA, categoryA, accountB, categoryB, expenseA, expenseB);

        await using var scope = fixture.CreateScope();
        var transactions = scope.ServiceProvider.GetRequiredService<ITransactionRepository>();

        var ofA = await transactions.GetByOccurredRangeAsync(a.Id, OccurredAtUtc.AddDays(-1), OccurredAtUtc.AddDays(1), CancellationToken.None);
        Assert.Equal(expenseA.Id, Assert.Single(ofA).Id);
    }

    [Fact]
    public async Task MonthlyAnalytics_RangeIsHalfOpenUserScopedAndKeepsCurrenciesApart()
    {
        // The real timestamptz comparison behind analytics: from inclusive, to exclusive, only the
        // caller's rows, and every currency's rows (the calculator keeps them apart).
        var (a, b) = await NewUsersAsync();
        var from = new DateTimeOffset(2026, 8, 31, 22, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.Zero);
        var accountA = NewAccount(a);
        var dollarsA = Account.Create(a.Id, "Dollars", AccountType.BankAccount, "USD", Now);
        var categoryA = TopLevel(a, "Travel");
        var accountB = NewAccount(b);
        var categoryB = TopLevel(b, "Travel");
        var atFrom = Transaction.CreateExpense(a.Id, accountA.Id, categoryA.Id, 1m, "EUR", from, null, Now);
        var lastMicrosecond = Transaction.CreateExpense(a.Id, accountA.Id, categoryA.Id, 10m, "EUR", to.AddTicks(-10), null, Now);
        var atTo = Transaction.CreateExpense(a.Id, accountA.Id, categoryA.Id, 100m, "EUR", to, null, Now);
        var beforeFrom = Transaction.CreateExpense(a.Id, accountA.Id, categoryA.Id, 1000m, "EUR", from.AddTicks(-10), null, Now);
        var inDollars = Transaction.CreateExpense(a.Id, dollarsA.Id, categoryA.Id, 5m, "USD", from.AddDays(3), null, Now);
        var ofB = Transaction.CreateExpense(b.Id, accountB.Id, categoryB.Id, 7m, "EUR", from.AddDays(3), null, Now);
        await PostgresAssert.InsertAsync(
            fixture, accountA, dollarsA, categoryA, accountB, categoryB, atFrom, lastMicrosecond, atTo, beforeFrom, inDollars, ofB);

        await using var scope = fixture.CreateScope();
        var result = await new GetMonthlyAnalyticsHandler(
                scope.ServiceProvider.GetRequiredService<ITransactionRepository>(),
                scope.ServiceProvider.GetRequiredService<ICategoryRepository>())
            .HandleAsync(a.Id, new(from, to), CancellationToken.None);

        Assert.Equal(["EUR", "USD"], result.Currencies.Select(currency => currency.Currency));
        Assert.Equal(11m, result.Currencies[0].Expenses);
        Assert.Equal(5m, result.Currencies[1].Expenses);
        Assert.Equal(categoryA.Id, Assert.Single(result.Currencies[0].ExpenseCategories).CategoryId);
    }

    [Fact]
    public async Task TransactionRepository_Recent_IsScopedOrderedAndLimitedByPostgreSql()
    {
        var (a, b) = await NewUsersAsync();
        var accountA = NewAccount(a);
        var categoryA = TopLevel(a, "Casa");
        var accountB = NewAccount(b);
        var categoryB = TopLevel(b, "Casa");
        var oldest = Transaction.CreateExpense(a.Id, accountA.Id, categoryA.Id, 1m, "EUR", OccurredAtUtc.AddDays(-2), null, Now);
        var tieCreatedEarlier = Transaction.CreateExpense(a.Id, accountA.Id, categoryA.Id, 2m, "EUR", OccurredAtUtc, null, Now);
        var tieCreatedLater = Transaction.CreateExpense(a.Id, accountA.Id, categoryA.Id, 3m, "EUR", OccurredAtUtc, null, Now.AddMinutes(1));
        var middle = Transaction.CreateExpense(a.Id, accountA.Id, categoryA.Id, 4m, "EUR", OccurredAtUtc.AddDays(-1), null, Now);
        var newestOfB = Transaction.CreateExpense(b.Id, accountB.Id, categoryB.Id, 5m, "EUR", OccurredAtUtc.AddDays(1), null, Now);
        await PostgresAssert.InsertAsync(
            fixture, accountA, categoryA, accountB, categoryB, oldest, tieCreatedEarlier, tieCreatedLater, middle, newestOfB);

        await using var scope = fixture.CreateScope();
        var recent = await scope.ServiceProvider.GetRequiredService<ITransactionRepository>()
            .GetRecentAsync(a.Id, 3, CancellationToken.None);

        Assert.Equal([tieCreatedLater.Id, tieCreatedEarlier.Id, middle.Id], recent.Select(transaction => transaction.Id));
    }

    // ---- Test 6: composite foreign keys reject cross-user references ----

    [Fact]
    public async Task CategoryWithAnotherUsersParent_IsRejected()
    {
        var (a, b) = await NewUsersAsync();
        var parentOfA = TopLevel(a, "Auto");
        await PostgresAssert.InsertAsync(fixture, parentOfA);

        // The Domain refuses to build this row, so it is written with SQL to reach the database backstop.
        await PostgresAssert.ViolatesAsync(
            PostgresAssert.ForeignKeyViolation,
            "FK_categories_categories_parent_category_id_user_id",
            async () =>
            {
                await using var scope = fixture.CreateScope();
                await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO categories (id, user_id, name, category_type, parent_category_id, created_at_utc)
                    VALUES ({Guid.CreateVersion7()}, {b.Id}, 'Benzina', 'Expense', {parentOfA.Id}, {Now})
                    """);
            });
    }

    [Fact]
    public async Task ExpenseWithAnotherUsersAccount_IsRejected()
    {
        var (a, b) = await NewUsersAsync();
        var accountA = NewAccount(a);
        var categoryB = TopLevel(b, "Casa");
        await PostgresAssert.InsertAsync(fixture, accountA, categoryB);

        await PostgresAssert.InsertViolatesAsync(
            fixture,
            PostgresAssert.ForeignKeyViolation,
            "FK_transactions_accounts_account_id_user_id",
            Transaction.CreateExpense(b.Id, accountA.Id, categoryB.Id, 1m, "EUR", OccurredAtUtc, null, Now));
    }

    [Fact]
    public async Task ExpenseWithAnotherUsersCategory_IsRejected()
    {
        var (a, b) = await NewUsersAsync();
        var categoryA = TopLevel(a, "Casa");
        var accountB = NewAccount(b);
        await PostgresAssert.InsertAsync(fixture, categoryA, accountB);

        await PostgresAssert.InsertViolatesAsync(
            fixture,
            PostgresAssert.ForeignKeyViolation,
            "FK_transactions_categories_category_id_user_id",
            Transaction.CreateExpense(b.Id, accountB.Id, categoryA.Id, 1m, "EUR", OccurredAtUtc, null, Now));
    }

    [Fact]
    public async Task TransferFromAnotherUsersAccount_IsRejected()
    {
        var (a, b) = await NewUsersAsync();
        var accountA = NewAccount(a);
        var accountB = NewAccount(b);
        await PostgresAssert.InsertAsync(fixture, accountA, accountB);

        await PostgresAssert.InsertViolatesAsync(
            fixture,
            PostgresAssert.ForeignKeyViolation,
            "FK_transactions_accounts_source_account_id_user_id",
            Transaction.CreateTransfer(b.Id, accountA.Id, accountB.Id, 1m, "EUR", OccurredAtUtc, null, Now));
    }

    [Fact]
    public async Task TransferToAnotherUsersAccount_IsRejected()
    {
        var (a, b) = await NewUsersAsync();
        var accountA = NewAccount(a);
        var accountB = NewAccount(b);
        await PostgresAssert.InsertAsync(fixture, accountA, accountB);

        await PostgresAssert.InsertViolatesAsync(
            fixture,
            PostgresAssert.ForeignKeyViolation,
            "FK_transactions_accounts_destination_account_id_user_id",
            Transaction.CreateTransfer(b.Id, accountB.Id, accountA.Id, 1m, "EUR", OccurredAtUtc, null, Now));
    }

    [Fact]
    public async Task SameUserReferences_AreAccepted()
    {
        var (a, _) = await NewUsersAsync();
        var checking = NewAccount(a);
        var savings = NewAccount(a);
        var auto = TopLevel(a, "Auto");
        var fuel = Category.Create(a.Id, "Benzina", CategoryType.Expense, auto, Now);

        await PostgresAssert.InsertAsync(
            fixture,
            checking, savings, auto, fuel,
            Transaction.CreateExpense(a.Id, checking.Id, fuel.Id, 40m, "EUR", OccurredAtUtc, null, Now),
            Transaction.CreateTransfer(a.Id, checking.Id, savings.Id, 100m, "EUR", OccurredAtUtc, null, Now));

        await using var scope = fixture.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        Assert.Equal(2, await dbContext.Transactions.CountAsync(transaction => transaction.UserId == a.Id));
    }

    private async Task<(User A, User B)> NewUsersAsync()
    {
        var a = User.CreateFromExternalIdentity(null, null, Now);
        var b = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, a, b);

        return (a, b);
    }

    private static Account NewAccount(User user) =>
        Account.Create(user.Id, "Checking " + Guid.NewGuid().ToString("N")[..6], AccountType.BankAccount, "EUR", Now);

    private static Category TopLevel(User user, string name) =>
        Category.Create(user.Id, name, CategoryType.Expense, parent: null, Now);
}
