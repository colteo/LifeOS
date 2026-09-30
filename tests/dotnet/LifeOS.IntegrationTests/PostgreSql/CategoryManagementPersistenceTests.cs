using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Finance.Categories.CreateCategory;
using LifeOS.Application.Finance.Categories.DeleteCategory;
using LifeOS.Application.Finance.Categories.RenameCategory;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Application.Finance.Transactions.CreateTransaction;
using LifeOS.Application.Onboarding;
using LifeOS.Application.Onboarding.SetUpFinanceProfile;
using LifeOS.Application.Users;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// Category management against real PostgreSQL: rename under the sibling-name index, delete under the
// restricting foreign keys, and both orders of every race. A race is made deterministic with a
// blocker: a competing write through the real repository, held uncommitted, until the operation under
// test waits for its lock. Final state is always read from a fresh scope.
[Collection(PostgreSqlCollection.Name)]
public class CategoryManagementPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 16, 0, 0, TimeSpan.Zero);

    // ---- Rename ----

    [Fact]
    public async Task Rename_ToASiblingsNameIgnoringCase_IsADuplicateAndChangesNothing()
    {
        var user = await NewUserAsync();
        var parent = TopLevel(user, "Food & Drink");
        var groceries = Child(user, "Groceries", parent);
        var eatingOut = Child(user, "Eating out", parent);
        await PostgresAssert.InsertAsync(fixture, parent, groceries, eatingOut);

        Assert.Equal(CategoryRenameOutcome.DuplicateName, await RenameWithRepositoryAsync(user.Id, eatingOut.Id, "GROCERIES"));

        Assert.Equal("Eating out", (await LoadAsync(user.Id)).Single(row => row.Id == eatingOut.Id).Name);
    }

    [Fact]
    public async Task Rename_ToANameUsedUnderAnotherParent_OrCaseOnlyOfItself_IsAllowed()
    {
        var user = await NewUserAsync();
        var foodAndDrink = TopLevel(user, "Food & Drink");
        var shopping = TopLevel(user, "Shopping");
        var groceries = Child(user, "Groceries", foodAndDrink);
        var clothing = Child(user, "Clothing", shopping);
        await PostgresAssert.InsertAsync(fixture, foodAndDrink, shopping, groceries, clothing);

        Assert.Equal(CategoryRenameOutcome.Renamed, await RenameWithRepositoryAsync(user.Id, clothing.Id, "Groceries"));
        Assert.Equal(CategoryRenameOutcome.Renamed, await RenameWithRepositoryAsync(user.Id, shopping.Id, "SHOPPING"));

        var stored = await LoadAsync(user.Id);
        Assert.Equal(("Groceries", (Guid?)shopping.Id), (stored.Single(row => row.Id == clothing.Id).Name, stored.Single(row => row.Id == clothing.Id).ParentCategoryId));
        Assert.Equal("SHOPPING", stored.Single(row => row.Id == shopping.Id).Name);
    }

    [Fact]
    public async Task Rename_WritesOnlyTheName_OfTheOwnersRow()
    {
        var (a, b) = (await NewUserAsync(), await NewUserAsync());
        var ofA = TopLevel(a, "Travel");
        var ofB = TopLevel(b, "Travel");
        await PostgresAssert.InsertAsync(fixture, ofA, ofB);

        Assert.Equal(CategoryRenameOutcome.Renamed, await RenameWithRepositoryAsync(a.Id, ofA.Id, "Trips"));

        var storedA = Assert.Single(await LoadAsync(a.Id));
        Assert.Equal(("Trips", CategoryType.Expense, (Guid?)null, ofA.CreatedAtUtc), (storedA.Name, storedA.CategoryType, storedA.ParentCategoryId, storedA.CreatedAtUtc));
        Assert.Equal("Travel", Assert.Single(await LoadAsync(b.Id)).Name);
    }

    // ---- Race 1: two renames to the same sibling name ----

    [Fact]
    public async Task Race_TwoRenamesToTheSameSiblingName_ExactlyOneWins()
    {
        var user = await NewUserAsync();
        var travel = TopLevel(user, "Travel");
        var leisure = TopLevel(user, "Leisure");
        await PostgresAssert.InsertAsync(fixture, travel, leisure);

        // Blocker: "Travel" is renamed to "Trips" but not committed; the handler's sibling check misses it.
        await using var blocker = await BlockerAsync();
        Assert.Equal(CategoryRenameOutcome.Renamed, await RenamedCopyAsync(blocker, user.Id, travel.Id, "Trips"));

        await using var scope = fixture.CreateScope();
        var rename = new RenameCategoryHandler(Categories(scope))
            .HandleAsync(user.Id, new RenameCategoryCommand(leisure.Id, "trips"), CancellationToken.None);

        // The second UPDATE waits on the uncommitted index entry, then the index rejects it.
        await WaitUntilASessionWaitsForALockAsync();
        await blocker.CommitAsync();

        Assert.Equal(RenameCategoryStatus.DuplicateName, (await rename).Status);
        var stored = await LoadAsync(user.Id);
        Assert.Equal("Trips", stored.Single(row => row.Id == travel.Id).Name);
        Assert.Equal("Leisure", stored.Single(row => row.Id == leisure.Id).Name);
    }

    // ---- Race 2: rename vs. delete of the same category (both orders) ----

    [Fact]
    public async Task Race_DeleteFirst_RenameWaitsThenReturnsNotFound()
    {
        var user = await NewUserAsync();
        var travel = TopLevel(user, "Travel");
        await PostgresAssert.InsertAsync(fixture, travel);

        await using var blocker = await BlockerAsync();
        Assert.Equal(CategoryDeleteOutcome.Deleted, await blocker.Categories.DeleteAsync(user.Id, travel.Id, CancellationToken.None));

        await using var scope = fixture.CreateScope();
        var rename = new RenameCategoryHandler(Categories(scope))
            .HandleAsync(user.Id, new RenameCategoryCommand(travel.Id, "Trips"), CancellationToken.None);

        await WaitUntilASessionWaitsForALockAsync();
        await blocker.CommitAsync();

        Assert.Equal(RenameCategoryStatus.NotFound, (await rename).Status);
        Assert.Empty(await LoadAsync(user.Id));
    }

    [Fact]
    public async Task Race_RenameFirst_DeleteWaitsThenDeletesTheRenamedRow()
    {
        var user = await NewUserAsync();
        var travel = TopLevel(user, "Travel");
        await PostgresAssert.InsertAsync(fixture, travel);

        await using var blocker = await BlockerAsync();
        Assert.Equal(CategoryRenameOutcome.Renamed, await RenamedCopyAsync(blocker, user.Id, travel.Id, "Trips"));

        await using var scope = fixture.CreateScope();
        var delete = new DeleteCategoryHandler(Categories(scope), Transactions(scope))
            .HandleAsync(user.Id, travel.Id, CancellationToken.None);

        await WaitUntilASessionWaitsForALockAsync();
        await blocker.CommitAsync();

        Assert.Equal(DeleteCategoryResult.Deleted, await delete);
        Assert.Empty(await LoadAsync(user.Id));
    }

    // ---- Delete: backstops without the handler's checks ----

    [Fact]
    public async Task Delete_ParentWithChildren_IsBlockedByTheParentForeignKey()
    {
        var user = await NewUserAsync();
        var travel = TopLevel(user, "Travel");
        var hotels = Child(user, "Hotels", travel);
        await PostgresAssert.InsertAsync(fixture, travel, hotels);

        Assert.Equal(CategoryDeleteOutcome.HasSubcategories, await DeleteWithRepositoryAsync(user.Id, travel.Id));

        Assert.Equal(2, (await LoadAsync(user.Id)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_UsedByATransaction_IsBlockedByTheTransactionForeignKey(bool subcategory)
    {
        var user = await NewUserAsync();
        var travel = TopLevel(user, "Travel");
        var hotels = Child(user, "Hotels", travel);
        var account = Account.Create(user.Id, "Main", AccountType.BankAccount, "EUR", Now);
        var used = subcategory ? hotels : TopLevel(user, "Leisure");
        await PostgresAssert.InsertAsync(fixture, account, travel, hotels);
        if (!subcategory)
        {
            await PostgresAssert.InsertAsync(fixture, used);
        }

        await PostgresAssert.InsertAsync(fixture, Expense(user, account, used));

        Assert.Equal(CategoryDeleteOutcome.InUse, await DeleteWithRepositoryAsync(user.Id, used.Id));

        Assert.Contains(await LoadAsync(user.Id), row => row.Id == used.Id);
    }

    [Fact]
    public async Task Delete_UnusedChildThenParent_AndAnotherUsersCategory()
    {
        var (a, b) = (await NewUserAsync(), await NewUserAsync());
        var travel = TopLevel(a, "Travel");
        var hotels = Child(a, "Hotels", travel);
        var ofB = TopLevel(b, "Travel");
        await PostgresAssert.InsertAsync(fixture, travel, hotels, ofB);

        Assert.Equal(CategoryDeleteOutcome.NotFound, await DeleteWithRepositoryAsync(a.Id, ofB.Id));
        Assert.Equal(CategoryDeleteOutcome.Deleted, await DeleteWithRepositoryAsync(a.Id, hotels.Id));
        Assert.Equal(CategoryDeleteOutcome.Deleted, await DeleteWithRepositoryAsync(a.Id, travel.Id));

        Assert.Empty(await LoadAsync(a.Id));
        Assert.Single(await LoadAsync(b.Id));
    }

    // ---- Race 3: delete parent vs. create subcategory (both orders) ----

    [Fact]
    public async Task Race_SubcategoryInsertedFirst_DeleteWaitsThenReportsHasSubcategories()
    {
        var user = await NewUserAsync();
        var travel = TopLevel(user, "Travel");
        await PostgresAssert.InsertAsync(fixture, travel);
        var hotels = Child(user, "Hotels", travel);

        await using var blocker = await BlockerAsync();
        Assert.True(await blocker.Categories.TryAddAsync(hotels, CancellationToken.None));

        await using var scope = fixture.CreateScope();
        var delete = new DeleteCategoryHandler(Categories(scope), Transactions(scope))
            .HandleAsync(user.Id, travel.Id, CancellationToken.None);

        await WaitUntilASessionWaitsForALockAsync();
        await blocker.CommitAsync();

        Assert.Equal(DeleteCategoryResult.HasSubcategories, await delete);
        Assert.Equal(new[] { hotels.Id, travel.Id }.Order(), (await LoadAsync(user.Id)).Select(row => row.Id).Order());
    }

    [Fact]
    public async Task Race_ParentDeletedFirst_SubcategoryInsertWaitsThenReturnsParentNotFound()
    {
        var user = await NewUserAsync();
        var travel = TopLevel(user, "Travel");
        await PostgresAssert.InsertAsync(fixture, travel);

        await using var blocker = await BlockerAsync();
        Assert.Equal(CategoryDeleteOutcome.Deleted, await blocker.Categories.DeleteAsync(user.Id, travel.Id, CancellationToken.None));

        await using var scope = fixture.CreateScope();
        var create = new CreateCategoryHandler(Categories(scope), new FixedTimeProvider(Now))
            .HandleAsync(user.Id, new CreateCategoryCommand("Hotels", CategoryType.Expense, travel.Id), CancellationToken.None);

        // The INSERT waits on the deleted parent row; once the delete commits, the parent key fails.
        await WaitUntilASessionWaitsForALockAsync();
        await blocker.CommitAsync();

        Assert.Equal(CreateCategoryStatus.ParentNotFound, (await create).Status);
        Assert.Empty(await LoadAsync(user.Id));
    }

    // ---- Race 4: delete category vs. create a transaction referencing it (both orders) ----

    [Fact]
    public async Task Race_TransactionInsertedFirst_DeleteWaitsThenReportsInUse()
    {
        var user = await NewUserAsync();
        var account = Account.Create(user.Id, "Main", AccountType.BankAccount, "EUR", Now);
        var travel = TopLevel(user, "Travel");
        await PostgresAssert.InsertAsync(fixture, account, travel);
        var expense = Expense(user, account, travel);

        await using var blocker = await BlockerAsync();
        Assert.True(await blocker.Transactions.TryAddAsync(expense, CancellationToken.None));

        await using var scope = fixture.CreateScope();
        var delete = new DeleteCategoryHandler(Categories(scope), Transactions(scope))
            .HandleAsync(user.Id, travel.Id, CancellationToken.None);

        await WaitUntilASessionWaitsForALockAsync();
        await blocker.CommitAsync();

        Assert.Equal(DeleteCategoryResult.InUse, await delete);
        Assert.Contains(await LoadAsync(user.Id), row => row.Id == travel.Id);
        Assert.Equal([expense.Id], await LoadTransactionIdsAsync(user.Id));
    }

    [Fact]
    public async Task Race_CategoryDeletedFirst_TransactionInsertWaitsThenReturnsNotFound()
    {
        var user = await NewUserAsync();
        var account = Account.Create(user.Id, "Main", AccountType.BankAccount, "EUR", Now);
        var travel = TopLevel(user, "Travel");
        await PostgresAssert.InsertAsync(fixture, account, travel);

        await using var blocker = await BlockerAsync();
        Assert.Equal(CategoryDeleteOutcome.Deleted, await blocker.Categories.DeleteAsync(user.Id, travel.Id, CancellationToken.None));

        await using var scope = fixture.CreateScope();
        var create = new CreateTransactionHandler(Accounts(scope), Categories(scope), Transactions(scope), new FixedTimeProvider(Now))
            .HandleAsync(
                user.Id,
                new CreateTransactionCommand(TransactionType.Expense, 80m, account.Id, null, null, travel.Id, Now, null),
                CancellationToken.None);

        await WaitUntilASessionWaitsForALockAsync();
        await blocker.CommitAsync();

        var result = await create;
        Assert.Equal(CreateTransactionStatus.NotFound, result.Status);
        Assert.Equal("categoryId", result.Field);
        Assert.Empty(await LoadTransactionIdsAsync(user.Id));
    }

    // ---- Onboarding: a reused parent deleted concurrently ----

    [Fact]
    public async Task Onboarding_ReusedParentDeletedConcurrently_IsReconciledIntoTheCompleteTree()
    {
        var user = await NewUserAsync();
        var foodAndDrink = TopLevel(user, "Food & Drink");
        await PostgresAssert.InsertAsync(fixture, foodAndDrink);

        // Blocker: the existing "Food & Drink" is deleted but not committed; setup reuses it as parent.
        await using var blocker = await BlockerAsync();
        Assert.Equal(CategoryDeleteOutcome.Deleted, await blocker.Categories.DeleteAsync(user.Id, foodAndDrink.Id, CancellationToken.None));

        await using var scope = fixture.CreateScope();
        var setUp = new SetUpFinanceProfileHandler(Users(scope), Categories(scope), new FixedTimeProvider(Now))
            .HandleAsync(user.Id, new SetUpFinanceProfileCommand("EUR"), CancellationToken.None);

        // The batch waits on the deleted parent; its parent key fails (recoverable), and the single
        // re-read creates "Food & Drink" again with its children.
        await WaitUntilASessionWaitsForALockAsync();
        await blocker.CommitAsync();

        Assert.Equal(OnboardingResultStatus.Ok, (await setUp).Status);
        var stored = await LoadAsync(user.Id);
        Assert.Equal(37, stored.Count);
        Assert.DoesNotContain(stored, row => row.Id == foodAndDrink.Id);
        Assert.True(StarterCategories.IsComplete(user.Id, stored));
        await using var verify = fixture.CreateScope();
        Assert.Equal(
            OnboardingStatus.PendingFirstAccount,
            (await verify.ServiceProvider.GetRequiredService<LifeOSDbContext>().Users.AsNoTracking().SingleAsync(row => row.Id == user.Id)).OnboardingStatus);
    }

    // A scope whose repositories write inside one open database transaction, committed by the test.
    private async Task<Blocker> BlockerAsync()
    {
        var scope = fixture.CreateScope();
        var transaction = await Db(scope).Database.BeginTransactionAsync();

        return new Blocker(scope, transaction);
    }

    private static async Task<CategoryRenameOutcome> RenamedCopyAsync(Blocker blocker, Guid userId, Guid categoryId, string name)
    {
        var category = (await blocker.Categories.GetByIdAsync(userId, categoryId, CancellationToken.None))!;
        category.Rename(name);

        return await blocker.Categories.TryRenameAsync(category, CancellationToken.None);
    }

    private async Task<CategoryRenameOutcome> RenameWithRepositoryAsync(Guid userId, Guid categoryId, string name)
    {
        await using var scope = fixture.CreateScope();
        var repository = Categories(scope);
        var category = (await repository.GetByIdAsync(userId, categoryId, CancellationToken.None))!;
        category.Rename(name);

        return await repository.TryRenameAsync(category, CancellationToken.None);
    }

    private async Task<CategoryDeleteOutcome> DeleteWithRepositoryAsync(Guid userId, Guid categoryId)
    {
        await using var scope = fixture.CreateScope();

        return await Categories(scope).DeleteAsync(userId, categoryId, CancellationToken.None);
    }

    private async Task<IReadOnlyList<Category>> LoadAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();

        return await Db(scope).Categories.AsNoTracking().Where(row => row.UserId == userId).ToListAsync();
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

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private static Category TopLevel(User user, string name) =>
        Category.Create(user.Id, name, CategoryType.Expense, parent: null, Now);

    private static Category Child(User user, string name, Category parent) =>
        Category.Create(user.Id, name, parent.CategoryType, parent, Now);

    private static Transaction Expense(User user, Account account, Category category) =>
        Transaction.CreateExpense(user.Id, account.Id, category.Id, 80m, "EUR", Now, null, Now);

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static IUserRepository Users(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IUserRepository>();

    private static IAccountRepository Accounts(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IAccountRepository>();

    private static ICategoryRepository Categories(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<ICategoryRepository>();

    private static ITransactionRepository Transactions(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ITransactionRepository>();

    private sealed class Blocker(AsyncServiceScope scope, IDbContextTransaction transaction) : IAsyncDisposable
    {
        public ICategoryRepository Categories => CategoryManagementPersistenceTests.Categories(scope);

        public ITransactionRepository Transactions => CategoryManagementPersistenceTests.Transactions(scope);

        public Task CommitAsync() => transaction.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await scope.DisposeAsync();
        }
    }
}
