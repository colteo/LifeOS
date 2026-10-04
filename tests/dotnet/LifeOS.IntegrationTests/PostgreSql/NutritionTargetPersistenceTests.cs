using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeOS.IntegrationTests.PostgreSql;

// NUT-003 against real PostgreSQL: the nutrition_targets schema from its migration, the database
// backstops (one state per user and day, owner, value/source checks), effective-target lookup, the
// same-day upsert and its concurrency, removal states and user isolation. Final state is always read
// from a fresh scope.
[Collection(PostgreSqlCollection.Name)]
public class NutritionTargetPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 10, 0, 0, TimeSpan.Zero);

    private static DateOnly Day(int october) => new(2026, 10, october);

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesNutritionTargets_AfterTheNut002Migration()
    {
        await using var scope = fixture.CreateScope();
        var database = Database(scope);
        var applied = (await database.GetAppliedMigrationsAsync()).ToList();

        Assert.EndsWith("_AddNutritionTargets", applied[^1]);
        Assert.EndsWith("_AddMealNutritionSnapshots", applied[^2]);
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "id uuid NOT NULL",
                "user_id uuid NOT NULL",
                "effective_from date NOT NULL",
                "calories_kcal numeric(6,1) NULL",
                "protein_grams numeric(5,1) NULL",
                "carbs_grams numeric(5,1) NULL",
                "fat_grams numeric(5,1) NULL",
                "source character varying(32) NOT NULL",
                "created_at_utc timestamp with time zone NOT NULL",
                "updated_at_utc timestamp with time zone NOT NULL"
            ],
            await Strings(database,
                """
                SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
                FROM pg_attribute
                WHERE attrelid = 'nutrition_targets'::regclass AND attnum > 0 AND NOT attisdropped
                ORDER BY attnum
                """));

        Assert.Contains("UNIQUE INDEX ux_nutrition_targets_user_effective_from ON public.nutrition_targets USING btree (user_id, effective_from)",
            await Scalar<string>(database, "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ux_nutrition_targets_user_effective_from'"));

        Assert.Equal(["ck_nutrition_targets_effective_from", "ck_nutrition_targets_source", "ck_nutrition_targets_values"], await Strings(database,
            "SELECT conname AS \"Value\" FROM pg_constraint WHERE conrelid = 'nutrition_targets'::regclass AND contype = 'c' ORDER BY 1"));

        // Owned by a user (restrict, like meal_entries); no other references, no AI/provider/body columns.
        Assert.Equal("FK_nutrition_targets_users_user_id users r", await Scalar<string>(database,
            """
            SELECT conname || ' ' || confrelid::regclass::text || ' ' || confdeltype::text AS "Value"
            FROM pg_constraint WHERE conrelid = 'nutrition_targets'::regclass AND contype = 'f'
            """));

        // NUT-001/002 tables are unchanged.
        Assert.Equal(9, await Scalar<int>(database,
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'meal_entries'"));
        Assert.Equal(9, await Scalar<int>(database,
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'meal_nutrition_snapshots'"));
    }

    [Fact]
    public async Task Migration_DownAndUp_DropsAndRecreatesOnlyNutritionTargets()
    {
        await using var scope = fixture.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        var migrator = dbContext.GetService<IMigrator>();
        var applied = (await dbContext.Database.GetAppliedMigrationsAsync()).ToList();
        var tablesBefore = await TableCountAsync(dbContext.Database);

        await migrator.MigrateAsync(applied[applied.FindIndex(id => id.EndsWith("_AddNutritionTargets", StringComparison.Ordinal)) - 1]);

        Assert.Equal(tablesBefore - 1, await TableCountAsync(dbContext.Database));
        Assert.Equal(0, await Scalar<int>(dbContext.Database,
            "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = 'nutrition_targets'"));
        Assert.Equal(1, await Scalar<int>(dbContext.Database,
            "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = 'meal_nutrition_snapshots'"));

        await migrator.MigrateAsync();

        Assert.Equal(applied, await dbContext.Database.GetAppliedMigrationsAsync());
        Assert.Equal(tablesBefore, await TableCountAsync(dbContext.Database));
    }

    // ---- Backstops ----

    [Fact]
    public async Task OneStatePerUserAndDay_IsAUniqueIndex()
    {
        var user = await NewUserAsync();
        await InsertRawAsync(user.Id, Day(4), "2200");

        await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, "ux_nutrition_targets_user_effective_from",
            () => InsertRawAsync(user.Id, Day(4), "2400"));

        // Another day, or another user on the same day, is fine.
        await InsertRawAsync(user.Id, Day(5), "2400");
        await InsertRawAsync((await NewUserAsync()).Id, Day(4), "2400");
    }

    [Fact]
    public async Task AStateNeedsAnExistingUser_AndBlocksDeletingThatUser()
    {
        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, "FK_nutrition_targets_users_user_id",
            () => InsertRawAsync(Guid.CreateVersion7(), Day(4), "2200"));

        var user = await NewUserAsync();
        await SaveAsync(Set(user.Id, Day(4), 2200));

        await PostgresAssert.DeleteBlockedAsync("FK_nutrition_targets_users_user_id", async () =>
        {
            await using var scope = fixture.CreateScope();
            await Database(scope).ExecuteSqlInterpolatedAsync($"DELETE FROM users WHERE id = {user.Id}");
        });
    }

    [Theory]
    [InlineData("ck_nutrition_targets_values", "0", "NULL", "Manual")]
    [InlineData("ck_nutrition_targets_values", "-1", "NULL", "Manual")]
    [InlineData("ck_nutrition_targets_values", "10000.1", "NULL", "Manual")]
    [InlineData("ck_nutrition_targets_values", "NULL", "0", "Manual")]
    [InlineData("ck_nutrition_targets_values", "NULL", "1000.1", "Manual")]
    [InlineData("ck_nutrition_targets_source", "2200", "NULL", "AiProposed")]
    public async Task CheckConstraints_RejectInvalidStates(string constraint, string calories, string protein, string source)
    {
        var user = await NewUserAsync();

        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, constraint,
            () => InsertRawAsync(user.Id, Day(4), calories, protein, source));
    }

    [Fact]
    public async Task AllNullMetrics_IsTheValidNoTargetsState()
    {
        var user = await NewUserAsync();

        await InsertRawAsync(user.Id, Day(4), "NULL");

        Assert.False((await EffectiveAsync(user.Id, Day(4)))!.HasTargets);
    }

    // ---- Round trip and lookup ----

    [Fact]
    public async Task RoundTrip_KeepsPartialValuesSourceAndTimes()
    {
        var user = await NewUserAsync();
        var target = Set(user.Id, Day(4), null, 160.4m, null, 70);

        await SaveAsync(target);

        var stored = (await EffectiveAsync(user.Id, Day(4)))!;
        Assert.Equal(target.Id, stored.Id);
        Assert.Equal((null, 160.4m, null, 70.0m), (stored.CaloriesKcal, stored.ProteinGrams, stored.CarbsGrams, stored.FatGrams));
        Assert.Equal(NutritionTargetSource.Manual, stored.Source);
        Assert.Equal(Day(4), stored.EffectiveFrom);
        Assert.Equal((Now, Now), (stored.CreatedAtUtc, stored.UpdatedAtUtc));
    }

    [Fact]
    public async Task Lookup_ReturnsTheLatestStateOnOrBeforeTheDate_ForHistoricalDaysToo()
    {
        var user = await NewUserAsync();
        await SaveAsync(Set(user.Id, Day(1), 2200));
        await SaveAsync(Set(user.Id, Day(18), 2400));
        await SaveAsync(NutritionTarget.Remove(user.Id, Day(25), NutritionTargetSource.Manual, Now));

        Assert.Null(await EffectiveAsync(user.Id, new DateOnly(2026, 9, 30)));
        Assert.Equal(2200m, (await EffectiveAsync(user.Id, Day(1)))!.CaloriesKcal);
        Assert.Equal(2200m, (await EffectiveAsync(user.Id, Day(17)))!.CaloriesKcal);
        Assert.Equal(2400m, (await EffectiveAsync(user.Id, Day(18)))!.CaloriesKcal);
        Assert.Equal(2400m, (await EffectiveAsync(user.Id, Day(24)))!.CaloriesKcal);
        Assert.False((await EffectiveAsync(user.Id, Day(25)))!.HasTargets);
        Assert.False((await EffectiveAsync(user.Id, new DateOnly(2027, 1, 1)))!.HasTargets);
        Assert.Equal(3, await CountAsync(user.Id));
    }

    [Fact]
    public async Task SameDaySave_UpdatesInPlace_KeepingIdAndCreationTime()
    {
        var user = await NewUserAsync();
        var first = Set(user.Id, Day(4), 2200, 160);
        await SaveAsync(first);
        await SaveAsync(Set(user.Id, Day(3), 2000));

        var later = NutritionTarget.Set(user.Id, Day(4), NutritionTargetValues.Create(null, 170, null, null), NutritionTargetSource.Manual,
            Now.AddHours(5));
        await SaveAsync(later);

        var stored = (await EffectiveAsync(user.Id, Day(4)))!;
        Assert.Equal(first.Id, stored.Id);
        Assert.Equal(Now, stored.CreatedAtUtc);
        Assert.Equal(Now.AddHours(5), stored.UpdatedAtUtc);
        Assert.Equal((null, 170m), (stored.CaloriesKcal, stored.ProteinGrams));
        Assert.Equal(2000m, (await EffectiveAsync(user.Id, Day(3)))!.CaloriesKcal);
        Assert.Equal(2, await CountAsync(user.Id));
    }

    [Fact]
    public async Task RemovingToday_KeepsEarlierStates()
    {
        var user = await NewUserAsync();
        await SaveAsync(Set(user.Id, Day(1), 2200));
        await SaveAsync(Set(user.Id, Day(4), 2400));

        await SaveAsync(NutritionTarget.Remove(user.Id, Day(4), NutritionTargetSource.Manual, Now));

        Assert.False((await EffectiveAsync(user.Id, Day(4)))!.HasTargets);
        Assert.Equal(2200m, (await EffectiveAsync(user.Id, Day(3)))!.CaloriesKcal);
        Assert.Equal(2, await CountAsync(user.Id));
    }

    [Fact]
    public async Task States_AreIsolatedPerUser()
    {
        var owner = await NewUserAsync();
        var other = await NewUserAsync();
        await SaveAsync(Set(owner.Id, Day(1), 2200));
        await SaveAsync(Set(other.Id, Day(4), 1800));

        Assert.Null(await EffectiveAsync(other.Id, Day(3)));
        Assert.Equal(1800m, (await EffectiveAsync(other.Id, Day(4)))!.CaloriesKcal);
        Assert.Equal(2200m, (await EffectiveAsync(owner.Id, Day(4)))!.CaloriesKcal);

        await SaveAsync(NutritionTarget.Remove(other.Id, Day(5), NutritionTargetSource.Manual, Now));
        Assert.Equal(2200m, (await EffectiveAsync(owner.Id, Day(5)))!.CaloriesKcal);
    }

    // ---- Concurrency ----

    [Fact]
    public async Task ConcurrentSameDaySets_LeaveExactlyOneState()
    {
        var user = await NewUserAsync();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(attempt => SaveAsync(Set(user.Id, Day(4), 2000 + attempt))));

        Assert.Equal(1, await CountAsync(user.Id));
        Assert.InRange((await EffectiveAsync(user.Id, Day(4)))!.CaloriesKcal!.Value, 2000m, 2007m);
    }

    [Fact]
    public async Task ConcurrentSetAndRemove_LeaveOneValidState()
    {
        var user = await NewUserAsync();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(attempt => SaveAsync(attempt % 2 == 0
            ? Set(user.Id, Day(4), 2200)
            : NutritionTarget.Remove(user.Id, Day(4), NutritionTargetSource.Manual, Now))));

        Assert.Equal(1, await CountAsync(user.Id));
        var state = (await EffectiveAsync(user.Id, Day(4)))!;
        Assert.True(state.HasTargets ? state.CaloriesKcal == 2200m : state.CaloriesKcal is null);
    }

    private static NutritionTarget Set(Guid userId, DateOnly day, decimal? kcal, decimal? protein = null, decimal? carbs = null, decimal? fat = null) =>
        NutritionTarget.Set(userId, day, NutritionTargetValues.Create(kcal, protein, carbs, fat), NutritionTargetSource.Manual, Now);

    private async Task SaveAsync(NutritionTarget target)
    {
        await using var scope = fixture.CreateScope();
        await Targets(scope).SaveAsync(target, CancellationToken.None);
    }

    private async Task<NutritionTarget?> EffectiveAsync(Guid userId, DateOnly date)
    {
        await using var scope = fixture.CreateScope();
        return await Targets(scope).GetEffectiveAsync(userId, date, CancellationToken.None);
    }

    private async Task<int> CountAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().NutritionTargets.CountAsync(target => target.UserId == userId);
    }

    private async Task InsertRawAsync(Guid userId, DateOnly day, string calories, string protein = "NULL", string source = "Manual")
    {
        await using var scope = fixture.CreateScope();
        await Database(scope).ExecuteSqlRawAsync(
            $"""
            INSERT INTO nutrition_targets (id, user_id, effective_from, calories_kcal, protein_grams, carbs_grams, fat_grams, source, created_at_utc, updated_at_utc)
            VALUES ('{Guid.CreateVersion7()}', '{userId}', DATE '{day:yyyy-MM-dd}', {calories}, {protein}, NULL, NULL, '{source}', now(), now())
            """);
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private static INutritionTargetRepository Targets(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<INutritionTargetRepository>();

    private static DatabaseFacade Database(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database;

    private static Task<int> TableCountAsync(DatabaseFacade database) => Scalar<int>(database,
        "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'");

    private static Task<T> Scalar<T>(DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<T>(sql).SingleAsync();

    private static Task<List<string>> Strings(DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
