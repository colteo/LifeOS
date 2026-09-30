using LifeOS.Application.Finance.Categories;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public class CategoryPersistenceTests(PostgreSqlFixture fixture)
{
    private const string SiblingNameIndex = "ux_categories_user_sibling_name";
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    // ---- Test 3: the migration-managed sibling-name index ----

    [Fact]
    public async Task Migration_CreatedTheExpressionIndexWithNullsNotDistinct()
    {
        await using var scope = fixture.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

        var definition = await dbContext.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = {SiblingNameIndex}")
            .SingleAsync();

        Assert.Contains("UNIQUE INDEX", definition);
        Assert.Contains("lower(name)", definition);
        Assert.Contains("NULLS NOT DISTINCT", definition);
    }

    [Fact]
    public async Task TopLevelNamesDifferingOnlyByCase_AreRejected()
    {
        var user = await NewUserAsync();
        await PostgresAssert.InsertAsync(fixture, TopLevel(user, "Casa", CategoryType.Expense));

        // Both parents are NULL: rejected only because of NULLS NOT DISTINCT.
        await PostgresAssert.InsertViolatesAsync(
            fixture, PostgresAssert.UniqueViolation, SiblingNameIndex, TopLevel(user, "casa", CategoryType.Expense));
    }

    [Fact]
    public async Task ChildNamesDifferingOnlyByCaseUnderTheSameParent_AreRejected()
    {
        var user = await NewUserAsync();
        var parent = TopLevel(user, "Mangiare fuori", CategoryType.Expense);
        await PostgresAssert.InsertAsync(fixture, parent, Child(user, "Bar", parent));

        await PostgresAssert.InsertViolatesAsync(
            fixture, PostgresAssert.UniqueViolation, SiblingNameIndex, Child(user, "BAR", parent));
    }

    [Fact]
    public async Task SameNameForDifferentUsers_IsAllowed()
    {
        var userA = await NewUserAsync();
        var userB = await NewUserAsync();

        await PostgresAssert.InsertAsync(
            fixture, TopLevel(userA, "Casa", CategoryType.Expense), TopLevel(userB, "Casa", CategoryType.Expense));
    }

    [Fact]
    public async Task SameNameForIncomeAndExpense_IsAllowed()
    {
        var user = await NewUserAsync();

        await PostgresAssert.InsertAsync(
            fixture, TopLevel(user, "Altro", CategoryType.Expense), TopLevel(user, "Altro", CategoryType.Income));
    }

    [Fact]
    public async Task SameChildNameUnderDifferentParents_IsAllowed()
    {
        var user = await NewUserAsync();
        var auto = TopLevel(user, "Auto", CategoryType.Expense);
        var casa = TopLevel(user, "Casa", CategoryType.Expense);

        await PostgresAssert.InsertAsync(fixture, auto, casa, Child(user, "Manutenzione", auto), Child(user, "Manutenzione", casa));
    }

    [Fact]
    public async Task TopLevelAndChildWithTheSameName_AreDifferentSiblingScopes()
    {
        var user = await NewUserAsync();
        var auto = TopLevel(user, "Auto", CategoryType.Expense);

        await PostgresAssert.InsertAsync(fixture, auto, TopLevel(user, "Casa", CategoryType.Expense), Child(user, "Casa", auto));
    }

    // ---- Test 4: CategoryRepository 23505 translation ----

    [Fact]
    public async Task TryAdd_SiblingConflict_ReturnsFalse_DetachesAndTheContextKeepsWorking()
    {
        var user = await NewUserAsync();
        await PostgresAssert.InsertAsync(fixture, TopLevel(user, "Casa", CategoryType.Expense));

        await using var scope = fixture.CreateScope();
        var repository = Repository(scope);
        var dbContext = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

        Assert.False(await repository.TryAddAsync(TopLevel(user, "CASA", CategoryType.Expense), CancellationToken.None));
        Assert.Equal(0, dbContext.PendingInserts<Category>());

        var valid = TopLevel(user, "Viaggi", CategoryType.Expense);
        Assert.True(await repository.TryAddAsync(valid, CancellationToken.None));

        await using var verify = fixture.CreateScope();
        var names = (await Repository(verify).GetAllAsync(user.Id, CancellationToken.None)).Select(category => category.Name).Order();
        Assert.Equal(["Casa", "Viaggi"], names);
    }

    [Fact]
    public async Task TryAddRange_SiblingConflict_PersistsNothingAndDetachesTheWholeBatch()
    {
        var user = await NewUserAsync();
        await PostgresAssert.InsertAsync(fixture, TopLevel(user, "Casa", CategoryType.Expense));

        await using var scope = fixture.CreateScope();
        var repository = Repository(scope);
        var dbContext = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        var batch = new[]
        {
            TopLevel(user, "Auto", CategoryType.Expense),
            TopLevel(user, "casa", CategoryType.Expense),
            TopLevel(user, "Stipendio", CategoryType.Income)
        };

        Assert.False(await repository.TryAddRangeAsync(batch, CancellationToken.None));
        Assert.Equal(0, dbContext.PendingInserts<Category>());
        Assert.All(batch, category => Assert.Equal(EntityState.Detached, dbContext.Entry(category).State));

        // A subsequent valid save on the same context inserts only its own rows.
        Assert.True(await repository.TryAddRangeAsync([TopLevel(user, "Salute", CategoryType.Expense)], CancellationToken.None));

        await using var verify = fixture.CreateScope();
        var names = (await Repository(verify).GetAllAsync(user.Id, CancellationToken.None)).Select(category => category.Name).Order();
        Assert.Equal(["Casa", "Salute"], names);
    }

    [Fact]
    public async Task TryAddRange_LosingADeadlock_ReturnsFalse_CommitsNothingAndTheContextKeepsWorking()
    {
        var user = await NewUserAsync();
        var (first, second) = InsertionOrderedPair(user, "Auto", "Casa");

        // Blocker: a competing transaction that holds an uncommitted "Casa" and later inserts "Auto".
        // Its long deadlock_timeout means only the batch's session detects the cycle, so the batch
        // is deterministically the deadlock victim.
        await using var blockerScope = fixture.CreateScope();
        var blocker = blockerScope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        await using var blockerTransaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlRawAsync("SET LOCAL deadlock_timeout = '30s'");
        await InsertCategoryRowAsync(blocker, user.Id, second.Name);

        await using var scope = fixture.CreateScope();
        var repository = Repository(scope);
        var dbContext = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

        // The batch inserts "Auto", then waits on the blocker's uncommitted "Casa".
        var batch = repository.TryAddRangeAsync([first, second], CancellationToken.None);
        await WaitUntilASessionWaitsForALockAsync();

        // The blocker now waits on the batch's "Auto": a cycle. The batch detects it and is aborted.
        var blockerInsert = InsertCategoryRowAsync(blocker, user.Id, first.Name);

        Assert.False(await batch);
        await blockerInsert;
        await blockerTransaction.RollbackAsync();

        // The failed batch is detached and the same context keeps working.
        Assert.Equal(EntityState.Detached, dbContext.Entry(first).State);
        Assert.Equal(EntityState.Detached, dbContext.Entry(second).State);
        Assert.Equal(0, dbContext.PendingInserts<Category>());
        Assert.True(await repository.TryAddRangeAsync([TopLevel(user, "Viaggi", CategoryType.Expense)], CancellationToken.None));

        // Nothing of the failed batch (nor of the rolled-back blocker) was committed.
        await using var verify = fixture.CreateScope();
        var names = (await Repository(verify).GetAllAsync(user.Id, CancellationToken.None)).Select(category => category.Name);
        Assert.Equal(["Viaggi"], names);
    }

    [Fact]
    public async Task TryAddRange_NewParentAndChild_ChildListedFirstWithTheLowerId_PersistsBoth()
    {
        // Onboarding inserts new parents and their children in one SaveChanges. EF Core orders the
        // inserts of one table by key, and UUID v7 ids are not monotonic within a millisecond, so the
        // child can sort before its parent. Only EF Core's foreign-key ordering keeps the parent's
        // INSERT first; otherwise PostgreSQL rejects the child (23503), which is not recovered.
        var user = await NewUserAsync();
        var (parent, child) = ChildSortingBeforeParent(user);
        Assert.True(child.Id.CompareTo(parent.Id) < 0);

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Repository(scope).TryAddRangeAsync([child, parent], CancellationToken.None));
        }

        await using var verify = fixture.CreateScope();
        var stored = await Repository(verify).GetAllAsync(user.Id, CancellationToken.None);
        Assert.Equal(2, stored.Count);
        Assert.Null(stored.Single(category => category.Id == parent.Id).ParentCategoryId);
        Assert.Equal(parent.Id, stored.Single(category => category.Id == child.Id).ParentCategoryId);
    }

    [Fact]
    public async Task TryAdd_PrimaryKeyViolation_StillThrows()
    {
        // Also 23505, but on PK_categories: not the sibling index, so it must propagate.
        var user = await NewUserAsync();
        var category = TopLevel(user, "Casa", CategoryType.Expense);
        await PostgresAssert.InsertAsync(fixture, category);

        await using var scope = fixture.CreateScope();

        await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, "PK_categories", () =>
            Repository(scope).TryAddAsync(category, CancellationToken.None));
    }

    [Fact]
    public async Task TryAdd_ForeignKeyViolation_StillThrows()
    {
        // The owning user does not exist.
        await using var scope = fixture.CreateScope();

        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, "FK_categories_users_user_id", () =>
            Repository(scope).TryAddAsync(
                Category.Create(Guid.CreateVersion7(), "Casa", CategoryType.Expense, parent: null, Now),
                CancellationToken.None));
    }

    // Two categories that the batch inserts in this order. EF Core orders the inserts of one table by
    // key, so the pair is chosen with ascending ids as well as in list order.
    private static (Category First, Category Second) InsertionOrderedPair(User user, string firstName, string secondName)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var first = TopLevel(user, firstName, CategoryType.Expense);
            var second = TopLevel(user, secondName, CategoryType.Expense);

            if (first.Id.CompareTo(second.Id) < 0)
            {
                return (first, second);
            }
        }

        throw new InvalidOperationException("Could not generate an ordered pair of category ids.");
    }

    // A new parent and child whose ids sort child first (same millisecond, random bits).
    private static (Category Parent, Category Child) ChildSortingBeforeParent(User user)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var parent = TopLevel(user, "Food & Drink", CategoryType.Expense);
            var child = Child(user, "Groceries", parent);

            if (child.Id.CompareTo(parent.Id) < 0)
            {
                return (parent, child);
            }
        }

        throw new InvalidOperationException("Could not generate a child id sorting before its parent id.");
    }

    private static Task<int> InsertCategoryRowAsync(LifeOSDbContext dbContext, Guid userId, string name) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO categories (id, user_id, name, category_type, parent_category_id, created_at_utc)
            VALUES ({Guid.CreateVersion7()}, {userId}, {name}, 'Expense', NULL, {Now})
            """);

    private async Task WaitUntilASessionWaitsForALockAsync()
    {
        await using var scope = fixture.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
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

        throw new TimeoutException("The batch never waited for the blocker's lock.");
    }

    private static ICategoryRepository Repository(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ICategoryRepository>();

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private static Category TopLevel(User user, string name, CategoryType categoryType) =>
        Category.Create(user.Id, name, categoryType, parent: null, Now);

    private static Category Child(User user, string name, Category parent) =>
        Category.Create(user.Id, name, parent.CategoryType, parent, Now);
}
