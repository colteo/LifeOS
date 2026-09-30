using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Accounts.DeleteAccount;
using LifeOS.Application.Finance.Accounts.SetOpeningBalance;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// Account management against real PostgreSQL: conditional update, atomic delete with the opening
// balance, and the restricting foreign keys as the final backstop, including both orders of a
// delete racing an insert that references the account. Final state is always read from a fresh scope.
[Collection(PostgreSqlCollection.Name)]
public class AccountManagementPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);

    // ---- Update ----

    [Fact]
    public async Task TryUpdate_WritesNameAndTypeOnly_AndLeavesOtherRowsAlone()
    {
        var (a, b) = await NewUsersAsync();
        var account = NewAccount(a, "Main");
        var other = NewAccount(a, "Other");
        var ofB = NewAccount(b, "Main");
        await PostgresAssert.InsertAsync(fixture, account, other, ofB);

        await using (var scope = fixture.CreateScope())
        {
            var repository = Accounts(scope);
            var loaded = (await repository.GetByIdAsync(a.Id, account.Id, CancellationToken.None))!;
            loaded.Rename("Everyday");
            loaded.ChangeType(AccountType.CreditCard);

            Assert.True(await repository.TryUpdateAsync(loaded, CancellationToken.None));
        }

        var stored = await LoadAccountsAsync();
        var updated = stored.Single(row => row.Id == account.Id);
        Assert.Equal(("Everyday", AccountType.CreditCard, "EUR"), (updated.Name, updated.AccountType, updated.Currency));
        Assert.Equal(account.CreatedAtUtc, updated.CreatedAtUtc);
        Assert.Equal("Other", stored.Single(row => row.Id == other.Id).Name);
        Assert.Equal(("Main", AccountType.BankAccount), (stored.Single(row => row.Id == ofB.Id).Name, stored.Single(row => row.Id == ofB.Id).AccountType));
    }

    [Fact]
    public async Task TryUpdate_OfADeletedRow_ReturnsFalse()
    {
        var (a, _) = await NewUsersAsync();
        var account = NewAccount(a, "Main");
        await PostgresAssert.InsertAsync(fixture, account);

        await using var scope = fixture.CreateScope();
        var repository = Accounts(scope);
        var loaded = (await repository.GetByIdAsync(a.Id, account.Id, CancellationToken.None))!;
        Assert.Equal(AccountDeleteOutcome.Deleted, await repository.DeleteAsync(a.Id, account.Id, CancellationToken.None));

        loaded.Rename("Too late");
        Assert.False(await repository.TryUpdateAsync(loaded, CancellationToken.None));
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_WithoutOpeningBalance_DeletesTheAccount()
    {
        var (a, _) = await NewUsersAsync();
        var account = NewAccount(a, "Main");
        var other = NewAccount(a, "Other");
        await PostgresAssert.InsertAsync(fixture, account, other);

        Assert.Equal(AccountDeleteOutcome.Deleted, await DeleteAsync(a.Id, account.Id));

        Assert.Equal([other.Id], (await LoadAccountsAsync()).Where(row => row.UserId == a.Id).Select(row => row.Id));
    }

    [Fact]
    public async Task Delete_WithOpeningBalance_DeletesBothAtomically()
    {
        var (a, _) = await NewUsersAsync();
        var account = NewAccount(a, "Main");
        var other = NewAccount(a, "Other");
        var othersBalance = OpeningBalance.Create(other, 5m, Now, Now);
        await PostgresAssert.InsertAsync(fixture, account, other, OpeningBalance.Create(account, -350m, Now, Now), othersBalance);

        Assert.Equal(AccountDeleteOutcome.Deleted, await DeleteAsync(a.Id, account.Id));

        Assert.DoesNotContain(await LoadAccountsAsync(), row => row.Id == account.Id);
        Assert.Equal([othersBalance.Id], (await LoadOpeningBalancesAsync(a.Id)).Select(row => row.Id));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("source")]
    [InlineData("destination")]
    public async Task Delete_ReferencedByATransaction_IsBlockedAndRollsBackTheOpeningBalanceDelete(string reference)
    {
        // Straight to the repository (no handler check): the foreign key alone must block it, and the
        // opening balance deleted earlier in the same database transaction must come back.
        var (a, _) = await NewUsersAsync();
        var account = NewAccount(a, "Main");
        var other = NewAccount(a, "Other");
        var category = Category.Create(a.Id, "Groceries", CategoryType.Expense, parent: null, Now);
        var openingBalance = OpeningBalance.Create(account, 100m, Now, Now);
        await PostgresAssert.InsertAsync(
            fixture, account, other, category, openingBalance, Referencing(reference, a.Id, account, other, category));

        Assert.Equal(AccountDeleteOutcome.HasTransactions, await DeleteAsync(a.Id, account.Id));

        Assert.Contains(await LoadAccountsAsync(), row => row.Id == account.Id);
        Assert.Equal([openingBalance.Id], (await LoadOpeningBalancesAsync(a.Id)).Where(row => row.AccountId == account.Id).Select(row => row.Id));
    }

    [Fact]
    public async Task Delete_AnotherUsersAccount_IsNotFoundAndDeletesNothing()
    {
        var (a, b) = await NewUsersAsync();
        var accountOfA = NewAccount(a, "Main");
        await PostgresAssert.InsertAsync(fixture, accountOfA, OpeningBalance.Create(accountOfA, 1m, Now, Now));

        Assert.Equal(AccountDeleteOutcome.NotFound, await DeleteAsync(b.Id, accountOfA.Id));

        Assert.Contains(await LoadAccountsAsync(), row => row.Id == accountOfA.Id);
        Assert.Single(await LoadOpeningBalancesAsync(a.Id));
    }

    [Fact]
    public async Task Delete_MissingAccount_IsNotFound()
    {
        var (a, _) = await NewUsersAsync();

        Assert.Equal(AccountDeleteOutcome.NotFound, await DeleteAsync(a.Id, Guid.CreateVersion7()));
    }

    // ---- Race: delete vs. a transaction referencing the account (both orders) ----

    [Fact]
    public async Task Race_TransactionInsertedFirst_DeleteWaitsThenReportsHasTransactions()
    {
        var (a, _) = await NewUsersAsync();
        var account = NewAccount(a, "Main");
        var category = Category.Create(a.Id, "Groceries", CategoryType.Expense, parent: null, Now);
        var openingBalance = OpeningBalance.Create(account, 100m, Now, Now);
        await PostgresAssert.InsertAsync(fixture, account, category, openingBalance);
        var expense = Transaction.CreateExpense(a.Id, account.Id, category.Id, 10m, "EUR", Now, null, Now);

        // Blocker: the transaction is inserted but not committed yet, so the handler's check misses it.
        await using var blockerScope = fixture.CreateScope();
        var blocker = Db(blockerScope);
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        blocker.Transactions.Add(expense);
        await blocker.SaveChangesAsync();

        await using var scope = fixture.CreateScope();
        var delete = new DeleteAccountHandler(Accounts(scope), Transactions(scope))
            .HandleAsync(a.Id, account.Id, CancellationToken.None);

        // The account DELETE waits for the uncommitted reference; once committed, the foreign key wins.
        await WaitUntilASessionWaitsForALockAsync();
        await blockerTransaction.CommitAsync();

        Assert.Equal(DeleteAccountResult.HasTransactions, await delete);
        Assert.Contains(await LoadAccountsAsync(), row => row.Id == account.Id);
        Assert.Equal([openingBalance.Id], (await LoadOpeningBalancesAsync(a.Id)).Select(row => row.Id));
        Assert.Equal([expense.Id], await LoadTransactionIdsAsync(a.Id));
    }

    [Fact]
    public async Task Race_AccountDeletedFirst_TransactionInsertWaitsThenReturnsNotFound()
    {
        var (a, _) = await NewUsersAsync();
        var account = NewAccount(a, "Main");
        var category = Category.Create(a.Id, "Groceries", CategoryType.Expense, parent: null, Now);
        await PostgresAssert.InsertAsync(fixture, account, category);

        // Blocker: the account is deleted but not committed yet, so the handler's lookups still find it.
        await using var blockerScope = fixture.CreateScope();
        var blocker = Db(blockerScope);
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Accounts.Where(row => row.Id == account.Id).ExecuteDeleteAsync();

        await using var scope = fixture.CreateScope();
        var create = new CreateTransactionHandler(Accounts(scope), Categories(scope), Transactions(scope), new FixedTimeProvider(Now))
            .HandleAsync(
                a.Id,
                new CreateTransactionCommand(TransactionType.Expense, 10m, account.Id, null, null, category.Id, Now, null),
                CancellationToken.None);

        // The INSERT waits on the deleted account row; once the delete commits, its foreign key fails.
        await WaitUntilASessionWaitsForALockAsync();
        await blockerTransaction.CommitAsync();

        var result = await create;
        Assert.Equal(CreateTransactionStatus.NotFound, result.Status);
        Assert.Equal("accountId", result.Field);
        Assert.Empty(await LoadTransactionIdsAsync(a.Id));
        Assert.DoesNotContain(await LoadAccountsAsync(), row => row.Id == account.Id);
    }

    // ---- Race: delete vs. a new opening balance (both orders) ----

    [Fact]
    public async Task Race_OpeningBalanceInsertedFirst_DeleteWaitsThenReportsChanged()
    {
        var (a, _) = await NewUsersAsync();
        var account = NewAccount(a, "Main");
        await PostgresAssert.InsertAsync(fixture, account);
        var openingBalance = OpeningBalance.Create(account, 100m, Now, Now);

        // Blocker: the opening balance is inserted but not committed, so the delete does not see it.
        await using var blockerScope = fixture.CreateScope();
        var blocker = Db(blockerScope);
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        blocker.OpeningBalances.Add(openingBalance);
        await blocker.SaveChangesAsync();

        await using var scope = fixture.CreateScope();
        var delete = new DeleteAccountHandler(Accounts(scope), Transactions(scope))
            .HandleAsync(a.Id, account.Id, CancellationToken.None);

        await WaitUntilASessionWaitsForALockAsync();
        await blockerTransaction.CommitAsync();

        Assert.Equal(DeleteAccountResult.Changed, await delete);
        Assert.Contains(await LoadAccountsAsync(), row => row.Id == account.Id);
        Assert.Equal([openingBalance.Id], (await LoadOpeningBalancesAsync(a.Id)).Select(row => row.Id));
    }

    [Fact]
    public async Task Race_AccountDeletedFirst_OpeningBalanceInsertWaitsThenReturnsNotFound()
    {
        var (a, _) = await NewUsersAsync();
        var account = NewAccount(a, "Main");
        await PostgresAssert.InsertAsync(fixture, account);

        await using var blockerScope = fixture.CreateScope();
        var blocker = Db(blockerScope);
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Accounts.Where(row => row.Id == account.Id).ExecuteDeleteAsync();

        await using var scope = fixture.CreateScope();
        var set = new SetOpeningBalanceHandler(Accounts(scope), OpeningBalances(scope), new FixedTimeProvider(Now))
            .HandleAsync(a.Id, new SetOpeningBalanceCommand(account.Id, 100m, Now), CancellationToken.None);

        await WaitUntilASessionWaitsForALockAsync();
        await blockerTransaction.CommitAsync();

        Assert.Equal(SetOpeningBalanceStatus.NotFound, (await set).Status);
        Assert.Empty(await LoadOpeningBalancesAsync(a.Id));
    }

    // ---- Unexpected integrity errors still propagate ----

    [Fact]
    public async Task TransactionTryAdd_PrimaryKeyViolation_StillThrows()
    {
        var (a, _) = await NewUsersAsync();
        var account = NewAccount(a, "Main");
        var category = Category.Create(a.Id, "Groceries", CategoryType.Expense, parent: null, Now);
        var expense = Transaction.CreateExpense(a.Id, account.Id, category.Id, 10m, "EUR", Now, null, Now);
        await PostgresAssert.InsertAsync(fixture, account, category, expense);

        await using var scope = fixture.CreateScope();

        await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, "PK_transactions", () =>
            Transactions(scope).TryAddAsync(expense, CancellationToken.None));
    }

    private static Transaction Referencing(string reference, Guid userId, Account account, Account other, Category category) =>
        reference switch
        {
            "account" => Transaction.CreateExpense(userId, account.Id, category.Id, 10m, "EUR", Now, null, Now),
            "source" => Transaction.CreateTransfer(userId, account.Id, other.Id, 10m, "EUR", Now, null, Now),
            _ => Transaction.CreateTransfer(userId, other.Id, account.Id, 10m, "EUR", Now, null, Now)
        };

    private async Task<AccountDeleteOutcome> DeleteAsync(Guid userId, Guid accountId)
    {
        await using var scope = fixture.CreateScope();

        return await Accounts(scope).DeleteAsync(userId, accountId, CancellationToken.None);
    }

    private async Task<IReadOnlyList<Account>> LoadAccountsAsync()
    {
        await using var scope = fixture.CreateScope();

        return await Db(scope).Accounts.AsNoTracking().ToListAsync();
    }

    private async Task<IReadOnlyList<OpeningBalance>> LoadOpeningBalancesAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();

        return await Db(scope).OpeningBalances.AsNoTracking().Where(row => row.UserId == userId).ToListAsync();
    }

    private async Task<IReadOnlyList<Guid>> LoadTransactionIdsAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();

        return await Db(scope).Transactions.AsNoTracking().Where(row => row.UserId == userId).Select(row => row.Id).ToListAsync();
    }

    private async Task WaitUntilASessionWaitsForALockAsync()
    {
        await using var scope = fixture.CreateScope();
        var dbContext = Db(scope);
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var waiting = await dbContext.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE NOT granted")
                .SingleAsync();

            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The operation never waited for the blocker's lock.");
    }

    private async Task<(User A, User B)> NewUsersAsync()
    {
        var a = User.CreateFromExternalIdentity(null, null, Now);
        var b = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, a, b);

        return (a, b);
    }

    private static Account NewAccount(User user, string name) =>
        Account.Create(user.Id, name, AccountType.BankAccount, "EUR", Now.AddDays(-10));

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static IAccountRepository Accounts(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IAccountRepository>();

    private static IOpeningBalanceRepository OpeningBalances(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IOpeningBalanceRepository>();

    private static ICategoryRepository Categories(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<ICategoryRepository>();

    private static ITransactionRepository Transactions(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ITransactionRepository>();
}
