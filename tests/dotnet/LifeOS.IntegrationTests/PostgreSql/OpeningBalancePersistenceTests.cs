using LifeOS.Application.Finance.Accounts;
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
public class OpeningBalancePersistenceTests(PostgreSqlFixture fixture)
{
    private const string AccountIndex = "ux_opening_balances_account_id";
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SecondOpeningBalanceForTheSameAccount_IsRejected()
    {
        var (user, _) = await NewUsersAsync();
        var account = NewAccount(user);
        await PostgresAssert.InsertAsync(fixture, account, OpeningBalance.Create(account, 100m, Now, Now));

        await PostgresAssert.InsertViolatesAsync(
            fixture, PostgresAssert.UniqueViolation, AccountIndex, OpeningBalance.Create(account, 200m, Now, Now));
    }

    [Fact]
    public async Task OpeningBalanceOnAnotherUsersAccount_IsRejectedByTheCompositeKey()
    {
        var (a, b) = await NewUsersAsync();
        var accountOfA = NewAccount(a);
        await PostgresAssert.InsertAsync(fixture, accountOfA);

        // The Domain takes the owner from the account, so the cross-user row is written with SQL.
        await PostgresAssert.ViolatesAsync(
            PostgresAssert.ForeignKeyViolation,
            "FK_opening_balances_accounts_account_id_user_id",
            async () =>
            {
                await using var scope = fixture.CreateScope();
                await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO opening_balances (id, user_id, account_id, amount, as_of_utc, created_at_utc)
                    VALUES ({Guid.CreateVersion7()}, {b.Id}, {accountOfA.Id}, 1, {Now}, {Now})
                    """);
            });
    }

    [Fact]
    public async Task NegativeAndZeroAmounts_AreStored()
    {
        var (user, _) = await NewUsersAsync();
        var card = NewAccount(user);
        var cash = NewAccount(user);

        await PostgresAssert.InsertAsync(
            fixture, card, cash, OpeningBalance.Create(card, -350.25m, Now, Now), OpeningBalance.Create(cash, 0m, Now, Now));

        await using var scope = fixture.CreateScope();
        var stored = await Repository(scope).GetAllAsync(user.Id, CancellationToken.None);
        Assert.Equal([-350.25m, 0m], stored.Select(openingBalance => openingBalance.Amount).Order());
    }

    [Fact]
    public async Task AccountWithOpeningBalance_CannotBeDeleted()
    {
        var (user, _) = await NewUsersAsync();
        var account = NewAccount(user);
        await PostgresAssert.InsertAsync(fixture, account, OpeningBalance.Create(account, 1m, Now, Now));

        await PostgresAssert.ViolatesAsync(
            PostgresAssert.RestrictViolation,
            "FK_opening_balances_accounts_account_id_user_id",
            async () =>
            {
                await using var scope = fixture.CreateScope();
                await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Accounts
                    .Where(stored => stored.Id == account.Id)
                    .ExecuteDeleteAsync();
            });
    }

    [Fact]
    public async Task AccountAndOpeningBalance_AreSavedTogetherOrNotAtAll()
    {
        var (user, _) = await NewUsersAsync();
        var existing = NewAccount(user);
        await PostgresAssert.InsertAsync(fixture, existing, OpeningBalance.Create(existing, 1m, Now, Now));

        // A new account whose opening balance collides with the existing one's unique index: nothing is saved.
        var account = NewAccount(user);
        var colliding = OpeningBalance.Create(existing, 2m, Now, Now);

        await using (var scope = fixture.CreateScope())
        {
            await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
                scope.ServiceProvider.GetRequiredService<IAccountRepository>().AddAsync(account, colliding, CancellationToken.None));
        }

        await using (var verify = fixture.CreateScope())
        {
            Assert.Null(await verify.ServiceProvider.GetRequiredService<IAccountRepository>()
                .GetByIdAsync(user.Id, account.Id, CancellationToken.None));
        }

        // The normal path saves both.
        var second = NewAccount(user);
        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAccountRepository>()
                .AddAsync(second, OpeningBalance.Create(second, 5m, Now, Now), CancellationToken.None);
        }

        await using var check = fixture.CreateScope();
        Assert.Equal(5m, (await Repository(check).GetByAccountIdAsync(user.Id, second.Id, CancellationToken.None))!.Amount);
    }

    [Fact]
    public async Task TryAdd_OnDuplicate_ReturnsFalseAndDetaches()
    {
        var (user, _) = await NewUsersAsync();
        var account = NewAccount(user);
        await PostgresAssert.InsertAsync(fixture, account, OpeningBalance.Create(account, 1m, Now, Now));

        await using var scope = fixture.CreateScope();
        var duplicate = OpeningBalance.Create(account, 2m, Now, Now);

        Assert.False(await Repository(scope).TryAddAsync(duplicate, CancellationToken.None));
        Assert.Equal(EntityState.Detached, scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Entry(duplicate).State);
    }

    [Fact]
    public async Task Repository_ReturnsOnlyTheUsersOpeningBalances()
    {
        var (a, b) = await NewUsersAsync();
        var accountOfA = NewAccount(a);
        var accountOfB = NewAccount(b);
        await PostgresAssert.InsertAsync(
            fixture, accountOfA, accountOfB, OpeningBalance.Create(accountOfA, 1m, Now, Now), OpeningBalance.Create(accountOfB, 2m, Now, Now));

        await using var scope = fixture.CreateScope();

        Assert.Equal(accountOfA.Id, Assert.Single(await Repository(scope).GetAllAsync(a.Id, CancellationToken.None)).AccountId);
        Assert.Null(await Repository(scope).GetByAccountIdAsync(b.Id, accountOfA.Id, CancellationToken.None));
    }

    [Fact]
    public async Task GetOccurredBefore_IsScopedAndExcludesTheInstantItself()
    {
        var (a, b) = await NewUsersAsync();
        var accountOfA = NewAccount(a);
        var accountOfB = NewAccount(b);
        var categoryOfA = Category.Create(a.Id, "Casa", CategoryType.Expense, parent: null, Now);
        var categoryOfB = Category.Create(b.Id, "Casa", CategoryType.Expense, parent: null, Now);
        var before = Transaction.CreateExpense(a.Id, accountOfA.Id, categoryOfA.Id, 1m, "EUR", Now.AddTicks(-10), null, Now);
        var atInstant = Transaction.CreateExpense(a.Id, accountOfA.Id, categoryOfA.Id, 2m, "EUR", Now, null, Now);
        var ofB = Transaction.CreateExpense(b.Id, accountOfB.Id, categoryOfB.Id, 3m, "EUR", Now.AddDays(-1), null, Now);
        await PostgresAssert.InsertAsync(fixture, accountOfA, accountOfB, categoryOfA, categoryOfB, before, atInstant, ofB);

        await using var scope = fixture.CreateScope();
        var transactions = await scope.ServiceProvider.GetRequiredService<ITransactionRepository>()
            .GetOccurredBeforeAsync(a.Id, Now, CancellationToken.None);

        Assert.Equal(before.Id, Assert.Single(transactions).Id);
    }

    private static IOpeningBalanceRepository Repository(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IOpeningBalanceRepository>();

    private async Task<(User A, User B)> NewUsersAsync()
    {
        var a = User.CreateFromExternalIdentity(null, null, Now);
        var b = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, a, b);

        return (a, b);
    }

    private static Account NewAccount(User user) =>
        Account.Create(user.Id, "Account " + Guid.NewGuid().ToString("N")[..6], AccountType.BankAccount, "EUR", Now.AddDays(-30));
}
