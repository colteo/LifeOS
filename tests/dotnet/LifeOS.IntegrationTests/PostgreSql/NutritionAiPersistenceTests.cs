using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeOS.IntegrationTests.PostgreSql;

// NUT-002 against real PostgreSQL: the meal_nutrition_snapshots schema from its migration, the
// database backstops (one snapshot per meal, cascade, value checks), the repository's user scoping,
// and the concurrency guarantees of bulk, lazy and explicit writes. Final state is always read from
// a fresh scope.
[Collection(PostgreSqlCollection.Name)]
public class NutritionAiPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 3);
    private static readonly DateOnly Yesterday = Today.AddDays(-1);

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesMealNutritionSnapshots_AfterTheMealJournal()
    {
        await using var scope = fixture.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database;
        var applied = (await database.GetAppliedMigrationsAsync()).ToList();

        // NUT-003's AddNutritionTargets may follow; NUT-002's migration comes right after NUT-001's.
        var snapshots = applied.FindIndex(id => id.EndsWith("_AddMealNutritionSnapshots", StringComparison.Ordinal));
        Assert.True(snapshots > 0);
        Assert.EndsWith("_AddNutritionMealEntries", applied[snapshots - 1]);
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "id uuid NOT NULL",
                "meal_entry_id uuid NOT NULL",
                "calories_kcal numeric(6,1) NOT NULL",
                "protein_grams numeric(5,1) NOT NULL",
                "carbs_grams numeric(5,1) NOT NULL",
                "fat_grams numeric(5,1) NOT NULL",
                "source character varying(16) NOT NULL",
                "created_at_utc timestamp with time zone NOT NULL",
                "updated_at_utc timestamp with time zone NOT NULL"
            ],
            await Strings(database,
                """
                SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
                FROM pg_attribute
                WHERE attrelid = 'meal_nutrition_snapshots'::regclass AND attnum > 0 AND NOT attisdropped
                ORDER BY attnum
                """));

        Assert.Contains("UNIQUE INDEX ux_meal_nutrition_snapshots_meal_entry ON public.meal_nutrition_snapshots USING btree (meal_entry_id)",
            await Scalar<string>(database, "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ux_meal_nutrition_snapshots_meal_entry'"));

        Assert.Equal(["ck_meal_nutrition_snapshots_source", "ck_meal_nutrition_snapshots_values"], await Strings(database,
            "SELECT conname AS \"Value\" FROM pg_constraint WHERE conrelid = 'meal_nutrition_snapshots'::regclass AND contype = 'c' ORDER BY 1"));

        // Part of the meal: cascades from meal_entries only; no user, provider, model or confidence columns.
        Assert.Equal("FK_meal_nutrition_snapshots_meal_entries_meal_entry_id meal_entries c", await Scalar<string>(database,
            """
            SELECT conname || ' ' || confrelid::regclass::text || ' ' || confdeltype::text AS "Value"
            FROM pg_constraint WHERE conrelid = 'meal_nutrition_snapshots'::regclass AND contype = 'f'
            """));

        // No day-state table and no nutrition columns on meal_entries.
        var tables = await Strings(database, "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'");
        Assert.DoesNotContain(tables, table => table.Contains("nutrition_day") || table.Contains("daily"));
        Assert.Equal(9, await Scalar<int>(database,
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'meal_entries'"));
    }

    [Fact]
    public async Task Migration_DownAndUp_DropsAndRecreatesOnlyTheSnapshots()
    {
        await using var scope = fixture.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        var migrator = dbContext.GetService<IMigrator>();
        var applied = (await dbContext.Database.GetAppliedMigrationsAsync()).ToList();
        var tablesBefore = await TableCountAsync(dbContext.Database);

        // Back to just before NUT-002; NUT-003's three target-plan tables and AUTO-001's automation_executions,
        // device_registrations and notification_deliveries, AUTO-002's weekly_reviews and
        // weekly_review_settings, AUTO-003A's notification_preferences and AI-001's weekly_review_insights
        // (later migrations) go too.
        await migrator.MigrateAsync(applied[applied.FindIndex(id => id.EndsWith("_AddMealNutritionSnapshots", StringComparison.Ordinal)) - 1]);

        Assert.Equal(0, await Scalar<int>(dbContext.Database,
            "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = 'nutrition_target_plans'"));
        Assert.Equal(tablesBefore - 11, await TableCountAsync(dbContext.Database));
        Assert.Equal(0, await Scalar<int>(dbContext.Database,
            "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = 'meal_nutrition_snapshots'"));
        Assert.Equal(1, await Scalar<int>(dbContext.Database,
            "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = 'meal_entries'"));

        await migrator.MigrateAsync();

        Assert.Equal(applied, await dbContext.Database.GetAppliedMigrationsAsync());
        Assert.Equal(tablesBefore, await TableCountAsync(dbContext.Database));
    }

    // ---- Backstops ----

    [Fact]
    public async Task OneSnapshotPerMeal_IsAUniqueIndex()
    {
        var meal = await MealAsync(await NewUserAsync(), "Pasta", Today);
        await InsertRawAsync(meal.Id, "620", "AiConfirmed");

        await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, "ux_meal_nutrition_snapshots_meal_entry",
            () => InsertRawAsync(meal.Id, "700", "UserAdjusted"));
    }

    [Fact]
    public async Task ASnapshotNeedsAMeal_AndDeletingTheMealDeletesIt()
    {
        var user = await NewUserAsync();
        var meal = await MealAsync(user, "Pasta", Today);
        await SaveAsync(user.Id, meal.Id, NutritionSource.AiConfirmed);

        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, "FK_meal_nutrition_snapshots_meal_entries_meal_entry_id",
            () => InsertRawAsync(Guid.CreateVersion7(), "1", "AiConfirmed"));

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<IMealEntryRepository>().DeleteAsync(user.Id, meal.Id, CancellationToken.None));
        }

        Assert.Equal(0, await SnapshotCountAsync(meal.Id));
    }

    [Theory]
    [InlineData("ck_meal_nutrition_snapshots_values", "-1", "1", "AiConfirmed")]
    [InlineData("ck_meal_nutrition_snapshots_values", "10000.1", "1", "AiConfirmed")]
    [InlineData("ck_meal_nutrition_snapshots_values", "1", "-0.1", "AiConfirmed")]
    [InlineData("ck_meal_nutrition_snapshots_values", "1", "1000.1", "AiConfirmed")]
    [InlineData("ck_meal_nutrition_snapshots_source", "1", "1", "Confident")]
    public async Task CheckConstraints_RejectInvalidValues(string constraint, string calories, string grams, string source)
    {
        var meal = await MealAsync(await NewUserAsync(), "Pasta", Today);

        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, constraint, () => InsertRawAsync(meal.Id, calories, source, grams));
    }

    [Fact]
    public async Task RoundTrip_KeepsValuesSourceAndTimes()
    {
        var user = await NewUserAsync();
        var meal = await MealAsync(user, "Pollo con le patate", Today);
        var snapshot = MealNutritionSnapshot.Create(meal.Id, NutritionValues.Create(620.04m, 52.25m, 58m, 19.95m), NutritionSource.AiConfirmed,
            Now.ToOffset(TimeSpan.FromHours(2)));

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Nutrition(scope).SaveAsync(user.Id, snapshot, CancellationToken.None));
        }

        await using var read = fixture.CreateScope();
        var stored = (await Nutrition(read).GetMealAsync(user.Id, meal.Id, CancellationToken.None))!.Nutrition!;
        Assert.Equal((snapshot.Id, meal.Id), (stored.Id, stored.MealEntryId));
        Assert.Equal((620.0m, 52.3m, 58.0m, 20.0m), (stored.CaloriesKcal, stored.ProteinGrams, stored.CarbsGrams, stored.FatGrams));
        Assert.Equal((NutritionSource.AiConfirmed, Now, Now), (stored.Source, stored.CreatedAtUtc, stored.UpdatedAtUtc));
        Assert.Equal("AiConfirmed", await Scalar<string>(Database(read), $"SELECT source AS \"Value\" FROM meal_nutrition_snapshots WHERE id = '{snapshot.Id}'"));
    }

    [Fact]
    public async Task Save_ReplacesTheCurrentSnapshot_KeepingItsIdentityAndCreation()
    {
        var user = await NewUserAsync();
        var meal = await MealAsync(user, "Pasta", Today);
        var first = await SaveAsync(user.Id, meal.Id, NutritionSource.AiConfirmed, 620);

        await using (var scope = fixture.CreateScope())
        {
            var adjusted = MealNutritionSnapshot.Create(meal.Id, NutritionValues.Create(500, 40, 40, 15), NutritionSource.UserAdjusted, Now.AddHours(1));
            Assert.True(await Nutrition(scope).SaveAsync(user.Id, adjusted, CancellationToken.None));
        }

        var stored = await SnapshotAsync(user.Id, meal.Id);
        Assert.Equal((first.Id, Now, Now.AddHours(1)), (stored!.Id, stored.CreatedAtUtc, stored.UpdatedAtUtc));
        Assert.Equal((500m, NutritionSource.UserAdjusted), (stored.CaloriesKcal, stored.Source));
    }

    [Fact]
    public async Task Save_RefusesBulkSources()
    {
        var user = await NewUserAsync();
        var meal = await MealAsync(user, "Pasta", Today);
        await using var scope = fixture.CreateScope();

        await Assert.ThrowsAsync<ArgumentException>(() => Nutrition(scope).SaveAsync(user.Id,
            MealNutritionSnapshot.Create(meal.Id, NutritionValues.Create(1, 1, 1, 1), NutritionSource.AiAutoClosed, Now), CancellationToken.None));
    }

    [Fact]
    public async Task AddIfMissing_NeverOverwrites_AndChecksTheEstimatedDescription()
    {
        var user = await NewUserAsync();
        var adjusted = await MealAsync(user, "Pasta", Yesterday);
        var changed = await MealAsync(user, "Riso", Yesterday);
        await SaveAsync(user.Id, adjusted.Id, NutritionSource.UserAdjusted, 222);

        await using (var scope = fixture.CreateScope())
        {
            Assert.False(await Nutrition(scope).AddIfMissingAsync(user.Id, Snapshot(adjusted.Id, NutritionSource.AiAutoClosed), "Pasta", CancellationToken.None));
            // Estimated for an older text: not stored for the current one.
            Assert.False(await Nutrition(scope).AddIfMissingAsync(user.Id, Snapshot(changed.Id, NutritionSource.AiAutoClosed), "Pasta e fagioli", CancellationToken.None));
            Assert.True(await Nutrition(scope).AddIfMissingAsync(user.Id, Snapshot(changed.Id, NutritionSource.AiAutoClosed), "Riso", CancellationToken.None));
        }

        Assert.Equal((222m, NutritionSource.UserAdjusted), ((await SnapshotAsync(user.Id, adjusted.Id))!.CaloriesKcal, (await SnapshotAsync(user.Id, adjusted.Id))!.Source));
        Assert.Equal(NutritionSource.AiAutoClosed, (await SnapshotAsync(user.Id, changed.Id))!.Source);
    }

    [Fact]
    public async Task MealUpdate_ClearsNutritionOnlyWhenAsked_InTheSameTransaction()
    {
        var user = await NewUserAsync();
        var kept = await MealAsync(user, "Pasta", Today);
        var cleared = await MealAsync(user, "Riso", Today);
        await SaveAsync(user.Id, kept.Id, NutritionSource.AiConfirmed);
        await SaveAsync(user.Id, cleared.Id, NutritionSource.AiConfirmed);

        await using (var scope = fixture.CreateScope())
        {
            var meals = scope.ServiceProvider.GetRequiredService<IMealEntryRepository>();
            var first = (await meals.GetAsync(user.Id, kept.Id, CancellationToken.None))!;
            first.Update("Pasta", MealType.Dinner, new TimeOnly(20, 0), Now);
            Assert.True(await meals.UpdateAsync(first, clearNutrition: false, CancellationToken.None));

            var second = (await meals.GetAsync(user.Id, cleared.Id, CancellationToken.None))!;
            second.Update("Riso e verdure", null, new TimeOnly(13, 0), Now);
            Assert.True(await meals.UpdateAsync(second, clearNutrition: true, CancellationToken.None));

            // Another user's forged update clears nothing.
            var forged = MealEntry.Create(Guid.CreateVersion7(), "Hacked", null, Today, new TimeOnly(9, 0), 0, Now);
            typeof(MealEntry).GetProperty(nameof(MealEntry.Id))!.SetValue(forged, kept.Id);
            Assert.False(await meals.UpdateAsync(forged, clearNutrition: true, CancellationToken.None));
        }

        Assert.NotNull(await SnapshotAsync(user.Id, kept.Id));
        Assert.Null(await SnapshotAsync(user.Id, cleared.Id));
    }

    // ---- User scoping and queries ----

    [Fact]
    public async Task EverythingIsUserScoped()
    {
        var owner = await NewUserAsync();
        var other = await NewUserAsync();
        var meal = await MealAsync(owner, "Pasta", Yesterday);

        await using (var scope = fixture.CreateScope())
        {
            var nutrition = Nutrition(scope);
            Assert.False(await nutrition.SaveAsync(other.Id, Snapshot(meal.Id, NutritionSource.UserAdjusted), CancellationToken.None));
            Assert.False(await nutrition.AddIfMissingAsync(other.Id, Snapshot(meal.Id, NutritionSource.AiAutoClosed), "Pasta", CancellationToken.None));
            Assert.Null(await nutrition.GetMealAsync(other.Id, meal.Id, CancellationToken.None));
            Assert.Empty(await nutrition.GetDayAsync(other.Id, Yesterday, CancellationToken.None));
            Assert.Empty(await nutrition.GetUnanalyzedBeforeAsync(other.Id, Today, 20, CancellationToken.None));
        }

        Assert.Null(await SnapshotAsync(owner.Id, meal.Id));
    }

    [Fact]
    public async Task Day_HasEachMealWithItsSnapshot_NewestFirst_AndTotalsAreExactSums()
    {
        var user = await NewUserAsync();
        var breakfast = await MealAsync(user, "Colazione", Today, new TimeOnly(8, 0));
        var lunch = await MealAsync(user, "Pranzo", Today, new TimeOnly(13, 0));
        await MealAsync(user, "Cena", Today, new TimeOnly(20, 0));
        var yesterday = await MealAsync(user, "Ieri", Yesterday);
        await SaveAsync(user.Id, breakfast.Id, NutritionSource.AiConfirmed, 740.1m, 52.2m, 80.3m, 20.4m);
        await SaveAsync(user.Id, lunch.Id, NutritionSource.UserAdjusted, 739.9m, 51.8m, 70.7m, 28.6m);
        await SaveAsync(user.Id, yesterday.Id, NutritionSource.UserAdjusted, 999, 99, 99, 99);

        await using var scope = fixture.CreateScope();
        var day = await Nutrition(scope).GetDayAsync(user.Id, Today, CancellationToken.None);
        var summary = (await new GetDailyNutritionSummaryHandler(Nutrition(scope), Targets(scope)).HandleAsync(user.Id, Today, CancellationToken.None)).Summary;

        Assert.Equal(["Cena", "Pranzo", "Colazione"], day.Select(meal => meal.Meal.Description));
        Assert.Equal([false, true, true], day.Select(meal => meal.Nutrition is not null));
        Assert.Equal(new DailyNutritionSummary(Today, 3, 2, 1480.0m, 104.0m, 151.0m, 49.0m), summary);
    }

    [Fact]
    public async Task Unanalyzed_IsPastOnly_Bounded_AndDeterministic()
    {
        var user = await NewUserAsync();
        await MealAsync(user, "Oggi", Today);
        await MealAsync(user, "Domani", Today.AddDays(1));
        var analyzed = await MealAsync(user, "Analizzato", Yesterday, new TimeOnly(7, 0));
        await SaveAsync(user.Id, analyzed.Id, NutritionSource.AiConfirmed);
        await MealAsync(user, "Ieri sera", Yesterday, new TimeOnly(21, 0));
        await MealAsync(user, "Ieri mattina", Yesterday, new TimeOnly(8, 0));
        await MealAsync(user, "Tre giorni fa", Today.AddDays(-3), new TimeOnly(12, 0));
        await MealAsync(user, "Due giorni fa", Today.AddDays(-2), new TimeOnly(12, 0));

        await using var scope = fixture.CreateScope();
        var all = await Nutrition(scope).GetUnanalyzedBeforeAsync(user.Id, Today, 20, CancellationToken.None);
        var bounded = await Nutrition(scope).GetUnanalyzedBeforeAsync(user.Id, Today, 2, CancellationToken.None);

        Assert.Equal(["Ieri mattina", "Ieri sera", "Due giorni fa", "Tre giorni fa"], all.Select(meal => meal.Description));
        Assert.Equal(["Ieri mattina", "Ieri sera"], bounded.Select(meal => meal.Description));
    }

    // ---- Concurrency ----

    [Fact]
    public async Task ConcurrentInserts_LeaveExactlyOneSnapshot()
    {
        var user = await NewUserAsync();
        var meal = await MealAsync(user, "Pasta", Yesterday);

        var inserted = await Task.WhenAll(Enumerable.Range(0, 8).Select(async attempt =>
        {
            await using var scope = fixture.CreateScope();
            return await Nutrition(scope).AddIfMissingAsync(user.Id,
                Snapshot(meal.Id, attempt % 2 == 0 ? NutritionSource.AiRequested : NutritionSource.AiAutoClosed, 100 + attempt), "Pasta",
                CancellationToken.None);
        }));

        Assert.Equal(1, inserted.Count(result => result));
        Assert.Equal(1, await SnapshotCountAsync(meal.Id));
    }

    [Fact]
    public async Task ConcurrentAnalyzeDayCalls_DoNotDuplicateSnapshots()
    {
        var user = await NewUserAsync();
        var meals = new List<MealEntry>();
        for (var hour = 8; hour < 12; hour++)
        {
            meals.Add(await MealAsync(user, $"Pasto {hour}", Today, new TimeOnly(hour, 0)));
        }

        var ai = new FakeNutritionEstimationService { BeforeAnswer = _ => Task.Delay(20) };

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var scope = fixture.CreateScope();
            var nutrition = Nutrition(scope);
            return await new AnalyzeDayHandler(nutrition, Targets(scope), new MealNutritionEstimation(ai, nutrition, TimeProvider.System))
                .HandleAsync(user.Id, Today, CancellationToken.None);
        }));

        Assert.Equal(4, results.Sum(result => result.Analysis!.Analyzed));
        foreach (var meal in meals)
        {
            Assert.Equal(1, await SnapshotCountAsync(meal.Id));
            Assert.Equal(NutritionSource.AiRequested, (await SnapshotAsync(user.Id, meal.Id))!.Source);
        }
    }

    [Fact]
    public async Task AnExplicitConfirmationRacingLazyClose_IsNeverOverwritten()
    {
        var user = await NewUserAsync();
        var meal = await MealAsync(user, "Pasta", Yesterday);

        // The confirmation is stored while lazy close is waiting for its estimate.
        var ai = new FakeNutritionEstimationService();
        ai.BeforeAnswer = async _ =>
        {
            ai.BeforeAnswer = null;
            await SaveAsync(user.Id, meal.Id, NutritionSource.AiConfirmed, 640);
        };

        await using (var scope = fixture.CreateScope())
        {
            var nutrition = Nutrition(scope);
            var result = await new LazyCloseNutritionHandler(nutrition, new MealNutritionEstimation(ai, nutrition, TimeProvider.System), TimeProvider.System)
                .CloseBeforeAsync(user.Id, Today, CancellationToken.None);
            Assert.Equal(0, result.Analyzed);
        }

        Assert.Equal((640m, NutritionSource.AiConfirmed), ((await SnapshotAsync(user.Id, meal.Id))!.CaloriesKcal, (await SnapshotAsync(user.Id, meal.Id))!.Source));
    }

    [Fact]
    public async Task ParallelExplicitAndLazyWrites_AlwaysEndWithTheExplicitResult()
    {
        var user = await NewUserAsync();
        var meals = new List<MealEntry>();
        for (var hour = 8; hour < 14; hour++)
        {
            meals.Add(await MealAsync(user, $"Pasto {hour}", Yesterday, new TimeOnly(hour, 0)));
        }

        await Task.WhenAll(meals.SelectMany(meal => new[]
        {
            Task.Run(async () =>
            {
                await using var scope = fixture.CreateScope();
                await Nutrition(scope).AddIfMissingAsync(user.Id, Snapshot(meal.Id, NutritionSource.AiAutoClosed, 111), meal.Description, CancellationToken.None);
            }),
            Task.Run(async () =>
            {
                await using var scope = fixture.CreateScope();
                Assert.True(await Nutrition(scope).SaveAsync(user.Id, Snapshot(meal.Id, NutritionSource.UserAdjusted, 999), CancellationToken.None));
            })
        }));

        foreach (var meal in meals)
        {
            var stored = await SnapshotAsync(user.Id, meal.Id);
            Assert.Equal((999m, NutritionSource.UserAdjusted), (stored!.CaloriesKcal, stored.Source));
            Assert.Equal(1, await SnapshotCountAsync(meal.Id));
        }
    }

    [Fact]
    public async Task ADescriptionChangeDuringLazyClose_LeavesTheMealUnanalyzed()
    {
        var user = await NewUserAsync();
        var meal = await MealAsync(user, "Pasta", Yesterday);
        var ai = new FakeNutritionEstimationService();
        ai.BeforeAnswer = async _ =>
        {
            ai.BeforeAnswer = null;
            await using var scope = fixture.CreateScope();
            var update = await new UpdateMealHandler(scope.ServiceProvider.GetRequiredService<IMealEntryRepository>(), Nutrition(scope), TimeProvider.System)
                .HandleAsync(user.Id, meal.Id, new UpdateMealCommand("Riso", null, new TimeOnly(13, 0)), CancellationToken.None);
            Assert.Equal(MealResultStatus.Ok, update.Status);
        };

        await using (var scope = fixture.CreateScope())
        {
            var nutrition = Nutrition(scope);
            await new LazyCloseNutritionHandler(nutrition, new MealNutritionEstimation(ai, nutrition, TimeProvider.System), TimeProvider.System)
                .CloseBeforeAsync(user.Id, Today, CancellationToken.None);
        }

        Assert.Null(await SnapshotAsync(user.Id, meal.Id));
    }

    // ---- Helpers ----

    private static MealNutritionSnapshot Snapshot(Guid mealId, NutritionSource source, decimal calories = 620) =>
        MealNutritionSnapshot.Create(mealId, NutritionValues.Create(calories, 52, 58, 20), source, Now);

    private async Task<MealNutritionSnapshot> SaveAsync(Guid userId, Guid mealId, NutritionSource source, decimal calories = 620,
        decimal protein = 52, decimal carbs = 58, decimal fat = 20)
    {
        var snapshot = MealNutritionSnapshot.Create(mealId, NutritionValues.Create(calories, protein, carbs, fat), source, Now);
        await using var scope = fixture.CreateScope();
        Assert.True(await Nutrition(scope).SaveAsync(userId, snapshot, CancellationToken.None));

        return snapshot;
    }

    private async Task<MealNutritionSnapshot?> SnapshotAsync(Guid userId, Guid mealId)
    {
        await using var scope = fixture.CreateScope();

        return (await Nutrition(scope).GetMealAsync(userId, mealId, CancellationToken.None))?.Nutrition;
    }

    private async Task<int> SnapshotCountAsync(Guid mealId)
    {
        await using var scope = fixture.CreateScope();

        return await Scalar<int>(Database(scope), $"SELECT count(*)::int AS \"Value\" FROM meal_nutrition_snapshots WHERE meal_entry_id = '{mealId}'");
    }

    private async Task InsertRawAsync(Guid mealId, string calories, string source, string grams = "10")
    {
        await using var scope = fixture.CreateScope();
        await Database(scope).ExecuteSqlRawAsync(
            $"""
            INSERT INTO meal_nutrition_snapshots (id, meal_entry_id, calories_kcal, protein_grams, carbs_grams, fat_grams, source, created_at_utc, updated_at_utc)
            VALUES ('{Guid.CreateVersion7()}', '{mealId}', {calories}, {grams}, 10, 10, '{source}', now(), now())
            """);
    }

    private async Task<MealEntry> MealAsync(User user, string description, DateOnly day, TimeOnly? time = null)
    {
        var meal = MealEntry.Create(user.Id, description, null, day, time ?? new TimeOnly(13, 0), 120, Now);
        await using var scope = fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMealEntryRepository>().AddAsync(meal, CancellationToken.None);

        return meal;
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private static IMealNutritionRepository Nutrition(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMealNutritionRepository>();

    private static INutritionTargetPlanRepository Targets(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<INutritionTargetPlanRepository>();

    private static Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade Database(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database;

    private static Task<int> TableCountAsync(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database) => Scalar<int>(database,
        "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'");

    private static Task<T> Scalar<T>(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<T>(sql).SingleAsync();

    private static Task<List<string>> Strings(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
