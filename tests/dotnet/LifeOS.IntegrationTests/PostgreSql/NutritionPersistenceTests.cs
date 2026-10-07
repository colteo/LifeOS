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

// NUT-001 against real PostgreSQL: the meal_entries schema from the migration, the repository's
// user scoping, diary-day filtering and newest-first order, and the database backstops. Final state is
// always read from a fresh scope.
[Collection(PostgreSqlCollection.Name)]
public class NutritionPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 3);

    [Fact]
    public async Task Migration_CreatesMealEntries_AfterTheGymMigrations()
    {
        await using var scope = fixture.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database;
        var applied = (await database.GetAppliedMigrationsAsync()).ToList();

        var nutrition = applied.FindIndex(id => id.EndsWith("_AddNutritionMealEntries", StringComparison.Ordinal));
        Assert.True(nutrition >= 0);
        Assert.True(applied.FindIndex(id => id.EndsWith("_AddGymActivePrograms", StringComparison.Ordinal)) < nutrition);
        Assert.False(database.HasPendingModelChanges());

        var columns = await Strings(database,
            """
            SELECT column_name || ' ' || data_type || ' ' || is_nullable AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'meal_entries'
            ORDER BY ordinal_position
            """);
        Assert.Equal(
            [
                "id uuid NO",
                "user_id uuid NO",
                "description text NO",
                "meal_type character varying YES",
                "diary_date date NO",
                "diary_time time without time zone NO",
                "occurred_at_utc timestamp with time zone NO",
                "created_at_utc timestamp with time zone NO",
                "updated_at_utc timestamp with time zone NO"
            ],
            columns);

        var index = await Scalar<string>(database,
            "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_meal_entries_user_diary'");
        Assert.Contains("(user_id, diary_date, diary_time DESC, id DESC)", index);

        var checks = await Strings(database,
            "SELECT conname AS \"Value\" FROM pg_constraint WHERE conrelid = 'meal_entries'::regclass AND contype = 'c' ORDER BY 1");
        Assert.Equal(
            ["ck_meal_entries_description", "ck_meal_entries_diary_date", "ck_meal_entries_diary_time", "ck_meal_entries_meal_type", "ck_meal_entries_utc_offset"],
            checks);

        // Owned by a user; a user with meals cannot be deleted (restrict, like the other modules).
        var ownerKey = await Scalar<string>(database,
            """
            SELECT conname || ' ' || confrelid::regclass::text || ' ' || confdeltype::text AS "Value"
            FROM pg_constraint WHERE conrelid = 'meal_entries'::regclass AND contype = 'f'
            """);
        Assert.Equal("FK_meal_entries_users_user_id users r", ownerKey);

        // No food-database or nutrition-value tables.
        var tables = await Strings(database,
            "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'");
        Assert.DoesNotContain(tables, table => table.Contains("food") || table.Contains("ingredient") || table.Contains("recipe") || table.Contains("serving"));
    }

    [Fact]
    public async Task Migration_DownAndUp_DropsAndRecreatesOnlyMealEntries()
    {
        await using var scope = fixture.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        var migrator = dbContext.GetService<IMigrator>();
        var applied = (await dbContext.Database.GetAppliedMigrationsAsync()).ToList();
        var tablesBefore = await TableCountAsync(dbContext.Database);

        // Back to just before NUT-001; the later NUT-002 meal_nutrition_snapshots, NUT-003 target-plan and AUTO-001
        // automation_executions, device_registrations and notification_deliveries, AUTO-002 weekly_reviews and
        // weekly_review_settings, AUTO-003A notification_preferences, JRN-001 journal_entries and AI-001
        // weekly_review_insights tables go first.
        await migrator.MigrateAsync(applied[applied.FindIndex(id => id.EndsWith("_AddNutritionMealEntries", StringComparison.Ordinal)) - 1]);

        Assert.Equal(0, await Scalar<int>(dbContext.Database,
            "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'meal_entries'"));
        Assert.Equal(tablesBefore - 13, await TableCountAsync(dbContext.Database));

        await migrator.MigrateAsync();

        Assert.Equal(applied, await dbContext.Database.GetAppliedMigrationsAsync());
        Assert.Equal(tablesBefore, await TableCountAsync(dbContext.Database));
    }

    [Fact]
    public async Task RoundTrip_KeepsTextLineBreaksOptionalTypeAndTimes()
    {
        var user = await NewUserAsync();
        var meal = MealEntry.Create(user.Id, "  200 g grilled chicken, basmati rice,\nzucchini and one tablespoon of olive oil ", MealType.Lunch,
            Today, new TimeOnly(13, 10), 120, Now);
        var snack = MealEntry.Create(user.Id, "Caffè", null, Today, new TimeOnly(10, 30), 120, Now);
        await AddAsync(meal, snack);

        await using var scope = fixture.CreateScope();
        var stored = (await Repository(scope).GetAsync(user.Id, meal.Id, CancellationToken.None))!;

        Assert.Equal("200 g grilled chicken, basmati rice,\nzucchini and one tablespoon of olive oil", stored.Description);
        Assert.Equal((MealType?)MealType.Lunch, stored.MealType);
        Assert.Equal((Today, new TimeOnly(13, 10)), (stored.DiaryDate, stored.DiaryTime));
        Assert.Equal(new DateTimeOffset(2026, 10, 3, 11, 10, 0, TimeSpan.Zero), stored.OccurredAtUtc);
        Assert.Equal(120, stored.UtcOffsetMinutes);
        Assert.Equal((Now, Now), (stored.CreatedAtUtc, stored.UpdatedAtUtc));

        Assert.Null((await Repository(scope).GetAsync(user.Id, snack.Id, CancellationToken.None))!.MealType);
        Assert.Equal("Lunch", await Scalar<string>(scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database,
            $"SELECT meal_type AS \"Value\" FROM meal_entries WHERE id = '{meal.Id}'"));
    }

    [Fact]
    public async Task DailyQuery_FiltersTheExactDiaryDay_NewestFirst_TiesByIdDescending()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();
        MealEntry Meal(string text, int hour, int minute, DateOnly? day = null, Guid? owner = null) =>
            MealEntry.Create(owner ?? user.Id, text, null, day ?? Today, new TimeOnly(hour, minute), 120, Now);

        var tieA = Meal("Tie A", 13, 10);
        var tieB = Meal("Tie B", 13, 10);
        await AddAsync(
            Meal("Breakfast", 8, 15), tieA, Meal("Dinner", 20, 15), tieB, Meal("Midnight", 0, 0),
            Meal("Yesterday", 23, 59, Today.AddDays(-1)), Meal("Tomorrow", 0, 0, Today.AddDays(1)),
            Meal("Other user", 12, 0, owner: other.Id));

        await using var scope = fixture.CreateScope();
        var meals = await Repository(scope).GetForDiaryDateAsync(user.Id, Today, CancellationToken.None);

        var ties = new[] { tieA, tieB }.OrderByDescending(meal => meal.Id).Select(meal => meal.Description);
        string[] expected = ["Dinner", .. ties, "Breakfast", "Midnight"];
        Assert.Equal(expected, meals.Select(meal => meal.Description));
        Assert.Equal(["Yesterday"], (await Repository(scope).GetForDiaryDateAsync(user.Id, Today.AddDays(-1), CancellationToken.None)).Select(meal => meal.Description));

        // The handler's order and the database's (uuid) order agree.
        var handler = await new GetMealsForDateHandler(scope.ServiceProvider.GetRequiredService<IMealNutritionRepository>()).HandleAsync(user.Id, Today, CancellationToken.None);
        Assert.Equal(meals.Select(meal => meal.Id), handler.Meals.Select(meal => meal.Id));
    }

    [Fact]
    public async Task DiaryDay_IsStable_ThoughTheStoredUtcInstantFallsOnAnotherDay()
    {
        var user = await NewUserAsync();
        // 00:30 in UTC+02:00 on the 3rd is 22:30 UTC on the 2nd; 23:30 in UTC-05:00 on the 3rd is
        // 04:30 UTC on the 4th.
        var early = MealEntry.Create(user.Id, "Latte caldo", null, Today, new TimeOnly(0, 30), 120, Now);
        var late = MealEntry.Create(user.Id, "Tisana", null, Today, new TimeOnly(23, 30), -300, Now);
        await AddAsync(early, late);

        await using var scope = fixture.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database;
        Assert.Equal(["2026-10-04 2026-10-03", "2026-10-02 2026-10-03"], await Strings(database,
            $"""
            SELECT to_char(occurred_at_utc AT TIME ZONE 'UTC', 'YYYY-MM-DD') || ' ' || to_char(diary_date, 'YYYY-MM-DD') AS "Value"
            FROM meal_entries WHERE user_id = '{user.Id}' ORDER BY diary_time DESC
            """));

        // The session time zone (a stand-in for "the phone moved") changes nothing.
        await database.ExecuteSqlRawAsync("SET TIME ZONE 'Pacific/Kiritimati'");
        var meals = await Repository(scope).GetForDiaryDateAsync(user.Id, Today, CancellationToken.None);
        Assert.Equal(["Tisana", "Latte caldo"], meals.Select(meal => meal.Description));
        Assert.Empty(await Repository(scope).GetForDiaryDateAsync(user.Id, Today.AddDays(-1), CancellationToken.None));
        Assert.Empty(await Repository(scope).GetForDiaryDateAsync(user.Id, Today.AddDays(1), CancellationToken.None));
        Assert.Equal(-300, meals[0].UtcOffsetMinutes);
    }

    [Fact]
    public async Task UpdateAndDelete_AreUserScoped()
    {
        var owner = await NewUserAsync();
        var other = await NewUserAsync();
        var meal = MealEntry.Create(owner.Id, "Pasta", MealType.Lunch, Today, new TimeOnly(13, 0), 120, Now);
        await AddAsync(meal);

        await using (var scope = fixture.CreateScope())
        {
            Assert.Null(await Repository(scope).GetAsync(other.Id, meal.Id, CancellationToken.None));
            Assert.False(await Repository(scope).DeleteAsync(other.Id, meal.Id, CancellationToken.None));

            // A forged entry with the same id but another owner updates nothing.
            var forged = MealEntry.Create(other.Id, "Hacked", null, Today, new TimeOnly(9, 0), 0, Now);
            typeof(MealEntry).GetProperty(nameof(MealEntry.Id))!.SetValue(forged, meal.Id);
            Assert.False(await Repository(scope).UpdateAsync(forged, clearNutrition: false, CancellationToken.None));
        }

        await using (var scope = fixture.CreateScope())
        {
            var stored = (await Repository(scope).GetAsync(owner.Id, meal.Id, CancellationToken.None))!;
            Assert.Equal("Pasta", stored.Description);

            stored.Update("Pasta al pomodoro", null, new TimeOnly(14, 0), Now.AddHours(1));
            Assert.True(await Repository(scope).UpdateAsync(stored, clearNutrition: false, CancellationToken.None));
        }

        await using (var scope = fixture.CreateScope())
        {
            var stored = (await Repository(scope).GetAsync(owner.Id, meal.Id, CancellationToken.None))!;
            Assert.Equal(("Pasta al pomodoro", (MealType?)null, new TimeOnly(14, 0), Today), (stored.Description, stored.MealType, stored.DiaryTime, stored.DiaryDate));
            Assert.Equal((Now, Now.AddHours(1)), (stored.CreatedAtUtc, stored.UpdatedAtUtc));
            Assert.Equal(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), stored.OccurredAtUtc);

            Assert.True(await Repository(scope).DeleteAsync(owner.Id, meal.Id, CancellationToken.None));
            Assert.Null(await Repository(scope).GetAsync(owner.Id, meal.Id, CancellationToken.None));
        }
    }

    [Fact]
    public async Task UserOwnership_IsAForeignKey_AndAUserWithMealsCannotBeDeleted()
    {
        await PostgresAssert.InsertViolatesAsync(fixture, PostgresAssert.ForeignKeyViolation, "FK_meal_entries_users_user_id",
            MealEntry.Create(Guid.CreateVersion7(), "Orphan", null, Today, new TimeOnly(9, 0), 0, Now));

        var user = await NewUserAsync();
        await AddAsync(MealEntry.Create(user.Id, "Pasta", null, Today, new TimeOnly(13, 0), 0, Now));

        await PostgresAssert.DeleteBlockedAsync("FK_meal_entries_users_user_id", async () =>
        {
            await using var scope = fixture.CreateScope();
            await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Users.Where(stored => stored.Id == user.Id).ExecuteDeleteAsync();
        });
    }

    [Theory]
    [InlineData("ck_meal_entries_description", "'   '", "NULL", "'2026-10-03'", "'13:10'", "'2026-10-03 11:10+00'")]
    [InlineData("ck_meal_entries_description", "repeat('a', 2001)", "NULL", "'2026-10-03'", "'13:10'", "'2026-10-03 11:10+00'")]
    [InlineData("ck_meal_entries_meal_type", "'Pasta'", "'Brunch'", "'2026-10-03'", "'13:10'", "'2026-10-03 11:10+00'")]
    [InlineData("ck_meal_entries_diary_date", "'Pasta'", "NULL", "'infinity'", "'13:10'", "'2026-10-03 11:10+00'")]
    [InlineData("ck_meal_entries_diary_time", "'Pasta'", "NULL", "'2026-10-03'", "'13:10:30'", "'2026-10-03 11:10:30+00'")]
    [InlineData("ck_meal_entries_utc_offset", "'Pasta'", "NULL", "'2026-10-03'", "'13:10'", "'2026-10-04 11:10+00'")]
    public async Task CheckConstraints_RejectInvalidRows(string constraint, string description, string mealType, string date, string time, string occurred)
    {
        var user = await NewUserAsync();

        await PostgresAssert.ViolatesAsync(PostgresErrorCodes.CheckViolation, constraint, async () =>
        {
            await using var scope = fixture.CreateScope();
            await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.ExecuteSqlRawAsync(
                $"""
                INSERT INTO meal_entries (id, user_id, description, meal_type, diary_date, diary_time, occurred_at_utc, created_at_utc, updated_at_utc)
                VALUES ('{Guid.CreateVersion7()}', '{user.Id}', {description}, {mealType}, {date}, {time}, {occurred}, now(), now())
                """);
        });
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now);
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private async Task AddAsync(params MealEntry[] meals)
    {
        foreach (var meal in meals)
        {
            await using var scope = fixture.CreateScope();
            await Repository(scope).AddAsync(meal, CancellationToken.None);
        }
    }

    private static IMealEntryRepository Repository(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMealEntryRepository>();

    private static Task<int> TableCountAsync(DatabaseFacade database) => Scalar<int>(database,
        "SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'");

    private static Task<T> Scalar<T>(DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<T>(sql).SingleAsync();

    private static Task<List<string>> Strings(DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
