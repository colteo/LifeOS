using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Accounts.DeleteAccount;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Finance.Transactions.UpdateTransaction;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// Transaction edit and delete against real PostgreSQL: conditional user-scoped writes, both shapes,
// and both orders of an edit racing the deletion of the account it moves to. Final state is always
// read from a fresh scope.
[Collection(PostgreSqlCollection.Name)]
public class TransactionManagementPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OccurredAt = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryUpdate_WritesEveryEditableColumn_ForBothShapes_AndOnlyTheOwnersRow()
    {
        var (a, b) = (await NewUserAsync(), await NewUserAsync());
        var checking = NewAccount(a, "Checking", "EUR");
        var dollars = NewAccount(a, "Dollars", "USD");
        var savings = NewAccount(a, "Savings", "EUR");
        var food = Category.Create(a.Id, "Food", CategoryType.Expense, parent: null, Now);
        var travel = Category.Create(a.Id, "Travel", CategoryType.Expense, parent: null, Now);
        var expense = Transaction.CreateExpense(a.Id, checking.Id, food.Id, 50m, "EUR", OccurredAt, "Lunch", Now);
        var transfer = Transaction.CreateTransfer(a.Id, checking.Id, savings.Id, 100m, "EUR", OccurredAt, null, Now);
        var accountOfB = NewAccount(b, "B", "EUR");
        var categoryOfB = Category.Create(b.Id, "B", CategoryType.Expense, parent: null, Now);
        var expenseOfB = Transaction.CreateExpense(b.Id, accountOfB.Id, categoryOfB.Id, 7m, "EUR", OccurredAt, "B", Now);
        await PostgresAssert.InsertAsync(fixture, checking, dollars, savings, food, travel, expense, transfer, accountOfB, categoryOfB, expenseOfB);
        var newTime = OccurredAt.AddDays(-1).AddTicks(1230); // microsecond precision survives

        await using (var scope = fixture.CreateScope())
        {
            var repository = Transactions(scope);
            var loadedExpense = (await repository.GetByIdAsync(a.Id, expense.Id, CancellationToken.None))!;
            loadedExpense.UpdateAccountTransaction(dollars.Id, travel.Id, 20.5m, "USD", newTime, "Dinner");
            var loadedTransfer = (await repository.GetByIdAsync(a.Id, transfer.Id, CancellationToken.None))!;
            loadedTransfer.UpdateTransfer(savings.Id, checking.Id, 75m, "EUR", newTime, "Back");

            Assert.Equal(TransactionUpdateOutcome.Updated, await repository.TryUpdateAsync(loadedExpense, CancellationToken.None));
            Assert.Equal(TransactionUpdateOutcome.Updated, await repository.TryUpdateAsync(loadedTransfer, CancellationToken.None));
        }

        var stored = await LoadAsync();
        var storedExpense = stored.Single(row => row.Id == expense.Id);
        Assert.Equal(
            (TransactionType.Expense, 20.5m, "USD", (Guid?)dollars.Id, (Guid?)travel.Id, (Guid?)null, "Dinner", newTime, expense.CreatedAtUtc),
            (storedExpense.TransactionType, storedExpense.Amount, storedExpense.Currency, storedExpense.AccountId, storedExpense.CategoryId,
                storedExpense.SourceAccountId, storedExpense.Note, storedExpense.OccurredAtUtc, storedExpense.CreatedAtUtc));
        var storedTransfer = stored.Single(row => row.Id == transfer.Id);
        Assert.Equal(
            (TransactionType.Transfer, 75m, (Guid?)savings.Id, (Guid?)checking.Id, (Guid?)null, (Guid?)null),
            (storedTransfer.TransactionType, storedTransfer.Amount, storedTransfer.SourceAccountId, storedTransfer.DestinationAccountId,
                storedTransfer.AccountId, storedTransfer.CategoryId));
        var storedOfB = stored.Single(row => row.Id == expenseOfB.Id);
        Assert.Equal((7m, "B"), (storedOfB.Amount, storedOfB.Note));
    }

    [Fact]
    public async Task TryUpdate_OfADeletedRow_IsNotFound_AndDeleteIsUserScoped()
    {
        var (a, b) = (await NewUserAsync(), await NewUserAsync());
        var checking = NewAccount(a, "Checking", "EUR");
        var food = Category.Create(a.Id, "Food", CategoryType.Expense, parent: null, Now);
        var expense = Transaction.CreateExpense(a.Id, checking.Id, food.Id, 50m, "EUR", OccurredAt, null, Now);
        await PostgresAssert.InsertAsync(fixture, checking, food, expense);

        await using var scope = fixture.CreateScope();
        var repository = Transactions(scope);
        var loaded = (await repository.GetByIdAsync(a.Id, expense.Id, CancellationToken.None))!;

        Assert.Null(await repository.GetByIdAsync(b.Id, expense.Id, CancellationToken.None));
        Assert.False(await repository.DeleteAsync(b.Id, expense.Id, CancellationToken.None));
        Assert.Single(await LoadAsync(), row => row.Id == expense.Id);

        Assert.True(await repository.DeleteAsync(a.Id, expense.Id, CancellationToken.None));
        Assert.False(await repository.DeleteAsync(a.Id, expense.Id, CancellationToken.None));

        loaded.UpdateAccountTransaction(checking.Id, food.Id, 5m, "EUR", OccurredAt, null);
        Assert.Equal(TransactionUpdateOutcome.NotFound, await repository.TryUpdateAsync(loaded, CancellationToken.None));
        Assert.DoesNotContain(await LoadAsync(), row => row.Id == expense.Id);
    }

    [Fact]
    public async Task Race_TargetAccountDeletedFirst_UpdateWaitsThenReturnsNotFoundForTheAccount()
    {
        var a = await NewUserAsync();
        var checking = NewAccount(a, "Checking", "EUR");
        var target = NewAccount(a, "Target", "EUR");
        var food = Category.Create(a.Id, "Food", CategoryType.Expense, parent: null, Now);
        var expense = Transaction.CreateExpense(a.Id, checking.Id, food.Id, 50m, "EUR", OccurredAt, null, Now);
        await PostgresAssert.InsertAsync(fixture, checking, target, food, expense);

        // Blocker: the target account is deleted but not committed; the handler's lookup still finds it.
        await using var blockerScope = fixture.CreateScope();
        var blocker = Db(blockerScope);
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Accounts.Where(row => row.Id == target.Id).ExecuteDeleteAsync();

        await using var scope = fixture.CreateScope();
        var update = new UpdateTransactionHandler(Accounts(scope), Categories(scope), Transactions(scope))
            .HandleAsync(
                a.Id,
                new UpdateTransactionCommand(expense.Id, 50m, OccurredAt, null, new AccountTransactionInput(target.Id, food.Id), null),
                CancellationToken.None);

        // The UPDATE waits on the deleted account row; once the delete commits, the reference fails.
        await WaitUntilASessionWaitsForALockAsync();
        await blockerTransaction.CommitAsync();

        var result = await update;
        Assert.Equal(UpdateTransactionStatus.NotFound, result.Status);
        Assert.Equal("accountTransaction.accountId", result.Field);
        Assert.Equal(checking.Id, (await LoadAsync()).Single(row => row.Id == expense.Id).AccountId);
    }

    [Fact]
    public async Task Race_UpdateFirst_TargetAccountDeleteWaitsThenReportsHasTransactions()
    {
        var a = await NewUserAsync();
        var checking = NewAccount(a, "Checking", "EUR");
        var target = NewAccount(a, "Target", "EUR");
        var food = Category.Create(a.Id, "Food", CategoryType.Expense, parent: null, Now);
        var expense = Transaction.CreateExpense(a.Id, checking.Id, food.Id, 50m, "EUR", OccurredAt, null, Now);
        await PostgresAssert.InsertAsync(fixture, checking, target, food, expense);

        // Blocker: the expense is moved to the target account but not committed yet.
        await using var blockerScope = fixture.CreateScope();
        await using var blockerTransaction = await Db(blockerScope).Database.BeginTransactionAsync();
        var blockerRepository = Transactions(blockerScope);
        var moving = (await blockerRepository.GetByIdAsync(a.Id, expense.Id, CancellationToken.None))!;
        moving.UpdateAccountTransaction(target.Id, food.Id, 50m, "EUR", OccurredAt, null);
        Assert.Equal(TransactionUpdateOutcome.Updated, await blockerRepository.TryUpdateAsync(moving, CancellationToken.None));

        await using var scope = fixture.CreateScope();
        var delete = new DeleteAccountHandler(Accounts(scope), Transactions(scope))
            .HandleAsync(a.Id, target.Id, CancellationToken.None);

        await WaitUntilASessionWaitsForALockAsync();
        await blockerTransaction.CommitAsync();

        Assert.Equal(DeleteAccountResult.HasTransactions, await delete);
        Assert.Equal(target.Id, (await LoadAsync()).Single(row => row.Id == expense.Id).AccountId);
    }

    private async Task<IReadOnlyList<Transaction>> LoadAsync()
    {
        await using var scope = fixture.CreateScope();

        return await Db(scope).Transactions.AsNoTracking().ToListAsync();
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

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private static Account NewAccount(User user, string name, string currency) =>
        Account.Create(user.Id, name, AccountType.BankAccount, currency, Now.AddDays(-30));

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static IAccountRepository Accounts(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IAccountRepository>();

    private static ICategoryRepository Categories(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<ICategoryRepository>();

    private static ITransactionRepository Transactions(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ITransactionRepository>();
}
