using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Domain.Nutrition.Hydration;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// HYD-001 §9 against real PostgreSQL: the hydration schema and its checks, the repository (totals per
// day, diary order, user isolation) and user deletion.
//
// The database is shared by the whole PostgreSQL collection, so every assertion is scoped to this
// test's own users.
[Collection(PostgreSqlCollection.Name)]
public class HydrationPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 10, 14);
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesTheIntakeTable()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddHydrationIntakes", StringComparison.Ordinal));
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "id uuid NOT NULL", "user_id uuid NOT NULL", "amount_ml integer NOT NULL", "diary_date date NOT NULL",
                "diary_time time without time zone NOT NULL", "occurred_at_utc timestamp with time zone NOT NULL",
                "created_at_utc timestamp with time zone NOT NULL", "updated_at_utc timestamp with time zone NOT NULL"
            ],
            await Columns(database, "hydration_intakes"));
        Assert.Equal(
            ["FK_hydration_intakes_users_user_id r", "ck_hydration_intakes_amount", "ck_hydration_intakes_diary_date",
             "ck_hydration_intakes_diary_time", "ck_hydration_intakes_utc_offset"],
            await Constraints(database, "hydration_intakes"));

        var index = (await Strings(database,
            "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ix_hydration_intakes_user_diary'")).Single();
        Assert.Contains("(user_id, diary_date, diary_time DESC, id DESC) INCLUDE (amount_ml)", index);

        // Totals are computed, never stored (P-1).
        Assert.DoesNotContain("total", string.Join(" ", await Columns(database, "hydration_intakes")));
    }

    [Fact]
    public async Task Migration_CreatesTheTargetAndSettingsTables()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddHydrationTargets", StringComparison.Ordinal));

        Assert.Equal(
            ["user_id uuid NOT NULL", "effective_from date NOT NULL", "target_ml integer NULL", "created_at_utc timestamp with time zone NOT NULL"],
            await Columns(database, "hydration_target_versions"));
        Assert.Equal(
            ["FK_hydration_target_versions_users_user_id r", "ck_hydration_target_versions_effective_from", "ck_hydration_target_versions_target"],
            await Constraints(database, "hydration_target_versions"));
        Assert.Equal(["PK_hydration_target_versions (user_id, effective_from)"], await PrimaryKey(database, "hydration_target_versions"));

        Assert.Equal(
            [
                "user_id uuid NOT NULL", "quick_add_1_ml integer NOT NULL", "quick_add_2_ml integer NOT NULL", "quick_add_3_ml integer NOT NULL",
                "updated_at_utc timestamp with time zone NOT NULL"
            ],
            await Columns(database, "hydration_settings"));
        Assert.Equal(["FK_hydration_settings_users_user_id r", "ck_hydration_settings_quick_add"], await Constraints(database, "hydration_settings"));
        Assert.Equal(["PK_hydration_settings (user_id)"], await PrimaryKey(database, "hydration_settings"));
    }

    // Down drops exactly the target and settings tables; Up recreates them.
    [Fact]
    public async Task Migration_DownAndUp_OfTargets_TouchOnlyItsTwoTables()
    {
        var user = await NewUserAsync();
        await UpsertTargetAsync(HydrationTargetVersion.Create(user.Id, Today, 2000, Now));
        await AddAsync(Intake(user.Id, 250, Today, 8, 0));

        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;
        var migrator = database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        var migrations = database.GetMigrations().ToList();
        var migration = migrations.Single(id => id.EndsWith("_AddHydrationTargets", StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(migration) - 1];
        var tablesBefore = await Tables(database);

        try
        {
            await migrator.MigrateAsync(previous);

            // AI-007's assistant tables come from a later migration, so they are rolled back too.
            Assert.Equal(tablesBefore.Except(["assistant_conversations", "assistant_messages", "assistant_operations", "hydration_settings", "hydration_target_versions"]),
                await Tables(database));
            Assert.Equal(["250"], await Strings(database,
                $"SELECT amount_ml::text AS \"Value\" FROM hydration_intakes WHERE user_id = '{user.Id}'"));
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        Assert.Equal(tablesBefore, await Tables(database));
    }

    // Down drops exactly the intake table and nothing else; Up recreates it. The collection runs one test
    // at a time, so the shared schema is complete again before the next test.
    [Fact]
    public async Task Migration_DownAndUp_TouchOnlyTheIntakeTable()
    {
        var user = await NewUserAsync();
        await AddAsync(Intake(user.Id, 250, Today, 8, 0));

        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;
        var migrator = database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        var migrations = database.GetMigrations().ToList();
        var migration = migrations.Single(id => id.EndsWith("_AddHydrationIntakes", StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(migration) - 1];
        var tablesBefore = await Tables(database);

        try
        {
            await migrator.MigrateAsync(previous);

            Assert.Equal(tablesBefore.Except(Hydration(tablesBefore)).Except(["assistant_conversations", "assistant_messages", "assistant_operations"]), await Tables(database));
            Assert.True(await Db(scope).Users.AnyAsync(stored => stored.Id == user.Id));
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        Assert.Equal(tablesBefore, await Tables(database));
        Assert.Empty(await database.GetPendingMigrationsAsync());

        // Rolling back AddHydrationIntakes also rolls back every later migration (H2's and AI-007's tables included).
        static IEnumerable<string> Hydration(IEnumerable<string> tables) => tables.Where(table => table.StartsWith("hydration_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidRows_AreRejectedByTheDatabase()
    {
        var user = await NewUserAsync();
        var intake = await AddAsync(Intake(user.Id, 250, Today, 8, 10));
        var id = intake.Id;

        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_hydration_intakes_amount",
            () => Execute($"UPDATE hydration_intakes SET amount_ml = 0 WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_hydration_intakes_amount",
            () => Execute($"UPDATE hydration_intakes SET amount_ml = 4001 WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_hydration_intakes_diary_time",
            () => Execute($"UPDATE hydration_intakes SET diary_time = '08:10:30', occurred_at_utc = occurred_at_utc + interval '30 seconds' WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_hydration_intakes_utc_offset",
            () => Execute($"UPDATE hydration_intakes SET occurred_at_utc = occurred_at_utc + interval '1 day' WHERE id = '{id}'"));
        await PostgresAssert.ViolatesOneOfAsync(PostgresAssert.CheckViolation, ["ck_hydration_intakes_diary_date", "ck_hydration_intakes_utc_offset"],
            () => Execute($"UPDATE hydration_intakes SET diary_date = 'infinity' WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, "FK_hydration_intakes_users_user_id",
            () => Execute($"UPDATE hydration_intakes SET user_id = '{Guid.CreateVersion7()}' WHERE id = '{id}'"));

        await UpsertTargetAsync(HydrationTargetVersion.Create(user.Id, Today, 2000, Now));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_hydration_target_versions_target",
            () => Execute($"UPDATE hydration_target_versions SET target_ml = 249 WHERE user_id = '{user.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_hydration_target_versions_target",
            () => Execute($"UPDATE hydration_target_versions SET target_ml = 10001 WHERE user_id = '{user.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_hydration_target_versions_effective_from",
            () => Execute($"UPDATE hydration_target_versions SET effective_from = '-infinity' WHERE user_id = '{user.Id}'"));

        await UpsertSettingsAsync(HydrationSettings.Create(user.Id, [200, 330, 750], Now));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_hydration_settings_quick_add",
            () => Execute($"UPDATE hydration_settings SET quick_add_2_ml = 0 WHERE user_id = '{user.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_hydration_settings_quick_add",
            () => Execute($"UPDATE hydration_settings SET quick_add_3_ml = 4001 WHERE user_id = '{user.Id}'"));
    }

    [Fact]
    public async Task DeletingAUserWithHydrationData_IsRestricted()
    {
        var user = await NewUserAsync();
        await AddAsync(Intake(user.Id, 250, Today, 8, 0));
        await PostgresAssert.DeleteBlockedAsync("FK_hydration_intakes_users_user_id", () => Execute($"DELETE FROM users WHERE id = '{user.Id}'"));

        var withTarget = await NewUserAsync();
        await UpsertTargetAsync(HydrationTargetVersion.Create(withTarget.Id, Today, null, Now));
        await PostgresAssert.DeleteBlockedAsync("FK_hydration_target_versions_users_user_id", () => Execute($"DELETE FROM users WHERE id = '{withTarget.Id}'"));

        var withSettings = await NewUserAsync();
        await UpsertSettingsAsync(HydrationSettings.Create(withSettings.Id, [150, 250, 500], Now));
        await PostgresAssert.DeleteBlockedAsync("FK_hydration_settings_users_user_id", () => Execute($"DELETE FROM users WHERE id = '{withSettings.Id}'"));
    }

    // ---- Repository ----

    [Fact]
    public async Task Intakes_RoundTrip_AndUpdateOnlyAmountAndTime()
    {
        var user = await NewUserAsync();
        var intake = await AddAsync(Intake(user.Id, 250, Today, 8, 10, offset: 120));

        var stored = (await GetAsync(user.Id, intake.Id))!;
        Assert.Equal((250, Today, new TimeOnly(8, 10), 120), (stored.AmountMl, stored.DiaryDate, stored.DiaryTime, stored.UtcOffsetMinutes));
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 6, 10, 0, TimeSpan.Zero), stored.OccurredAtUtc);

        stored.Update(500, new TimeOnly(9, 45), Now.AddHours(1));
        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Repository(scope).SaveAsync(stored, CancellationToken.None));
        }

        var updated = (await GetAsync(user.Id, intake.Id))!;
        Assert.Equal((500, Today, new TimeOnly(9, 45), 120), (updated.AmountMl, updated.DiaryDate, updated.DiaryTime, updated.UtcOffsetMinutes));
        Assert.Equal((stored.CreatedAtUtc, Now.AddHours(1)), (updated.CreatedAtUtc, updated.UpdatedAtUtc));
    }

    [Fact]
    public async Task Intakes_AreIsolatedByUser()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();
        var intake = await AddAsync(Intake(user.Id, 250, Today, 8, 0));
        await AddAsync(Intake(other.Id, 1000, Today, 9, 0));

        await using var scope = fixture.CreateScope();
        var repository = Repository(scope);

        Assert.Null(await repository.GetAsync(other.Id, intake.Id, CancellationToken.None));
        Assert.False(await repository.DeleteAsync(other.Id, intake.Id, CancellationToken.None));
        Assert.Equal([250], (await repository.GetForDateAsync(user.Id, Today, CancellationToken.None)).Select(stored => stored.AmountMl));
        Assert.Equal(250, (await repository.GetDailyTotalsAsync(user.Id, Today, Today, CancellationToken.None))[Today]);

        var foreign = HydrationIntake.Create(other.Id, 4000, Today, new TimeOnly(8, 0), 0, Now);
        Assert.False(await repository.SaveAsync(foreign, CancellationToken.None));

        Assert.True(await repository.DeleteAsync(user.Id, intake.Id, CancellationToken.None));
        Assert.Empty(await repository.GetForDateAsync(user.Id, Today, CancellationToken.None));
    }

    [Fact]
    public async Task Day_IsInDiaryOrder_NewestFirst_ThenByIdDescending()
    {
        var user = await NewUserAsync();
        var first = await AddAsync(Intake(user.Id, 250, Today, 10, 45));
        var second = await AddAsync(Intake(user.Id, 500, Today, 10, 45));
        var earliest = await AddAsync(Intake(user.Id, 150, Today, 8, 10));
        var latest = await AddAsync(Intake(user.Id, 330, Today, 13, 20));
        await AddAsync(Intake(user.Id, 750, Today.AddDays(-1), 23, 59));

        await using var scope = fixture.CreateScope();
        var day = await Repository(scope).GetForDateAsync(user.Id, Today, CancellationToken.None);

        Assert.Equal([latest.Id, second.Id, first.Id, earliest.Id], day.Select(intake => intake.Id));
    }

    [Fact]
    public async Task DailyTotals_SumEveryIntakePerDiaryDate_InTheRange()
    {
        var user = await NewUserAsync();

        for (var i = 0; i < 40; i++)
        {
            await AddAsync(Intake(user.Id, 250, Today, 6 + i / 4, i % 4 * 15));
        }

        await AddAsync(Intake(user.Id, 500, Today.AddDays(-2), 9, 0));
        await AddAsync(Intake(user.Id, 750, Today.AddDays(-2), 19, 0));
        await AddAsync(Intake(user.Id, 1000, Today.AddDays(-7), 9, 0));
        await AddAsync(Intake(user.Id, 1000, Today.AddDays(1), 9, 0));

        await using var scope = fixture.CreateScope();
        var totals = await Repository(scope).GetDailyTotalsAsync(user.Id, Today.AddDays(-6), Today, CancellationToken.None);

        Assert.Equal(new Dictionary<DateOnly, int> { [Today.AddDays(-2)] = 1250, [Today] = 10000 }, totals);

        // The reader fills the empty days of the range with zeros.
        var days = await new HydrationDayReader(Repository(scope))
            .GetDaysAsync(user.Id, Today.AddDays(-2), Today, CancellationToken.None);
        Assert.Equal([1250, 0, 10000], days.Select(day => day.TotalMl));
    }

    [Fact]
    public async Task TargetVersions_UpsertOnTheSameDate_AndStayPerUser()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();

        await UpsertTargetAsync(HydrationTargetVersion.Create(user.Id, Today.AddDays(-10), 2000, Now.AddDays(-10)));
        await UpsertTargetAsync(HydrationTargetVersion.Create(user.Id, Today, 2500, Now));
        await UpsertTargetAsync(HydrationTargetVersion.Create(user.Id, Today, null, Now.AddMinutes(5)));
        await UpsertTargetAsync(HydrationTargetVersion.Create(other.Id, Today.AddDays(-3), 3000, Now));

        await using var scope = fixture.CreateScope();
        var repository = Repository(scope);
        var versions = await repository.GetTargetVersionsAsync(user.Id, Today, CancellationToken.None);

        Assert.Equal([(Today.AddDays(-10), (int?)2000), (Today, null)], versions.Select(version => (version.EffectiveFrom, version.TargetMl)));
        Assert.Equal(Now.AddMinutes(5), versions[1].CreatedAtUtc);
        Assert.Equal([Today.AddDays(-10)], (await repository.GetTargetVersionsAsync(user.Id, Today.AddDays(-1), CancellationToken.None))
            .Select(version => version.EffectiveFrom));

        // Days through the reader: before the first version none, then 2000, then removed from today.
        await AddAsync(Intake(user.Id, 2000, Today.AddDays(-1), 9, 0));
        var days = await new HydrationDayReader(repository).GetDaysAsync(user.Id, Today.AddDays(-11), Today, CancellationToken.None);
        Assert.Equal([null, 2000, 2000, 2000, 2000, 2000, 2000, 2000, 2000, 2000, 2000, null], days.Select(day => day.TargetMl));
        Assert.True(days[^2].IsTargetMet);
    }

    [Fact]
    public async Task Settings_DefaultWithoutARow_ThenUpsert_AndStayPerUser()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();

        await using (var scope = fixture.CreateScope())
        {
            Assert.Equal([150, 250, 500], (await Repository(scope).GetSettingsAsync(user.Id, CancellationToken.None)).QuickAddMl);
        }

        await UpsertSettingsAsync(HydrationSettings.Create(user.Id, [200, 330, 750], Now));
        await UpsertSettingsAsync(HydrationSettings.Create(user.Id, [250, 250, 1000], Now.AddHours(1)));

        await using (var scope = fixture.CreateScope())
        {
            var settings = await Repository(scope).GetSettingsAsync(user.Id, CancellationToken.None);
            Assert.Equal([250, 250, 1000], settings.QuickAddMl);
            Assert.Equal(Now.AddHours(1), settings.UpdatedAtUtc);
            Assert.Equal([150, 250, 500], (await Repository(scope).GetSettingsAsync(other.Id, CancellationToken.None)).QuickAddMl);
        }
    }

    // ---- Helpers ----

    private async Task UpsertTargetAsync(HydrationTargetVersion version)
    {
        await using var scope = fixture.CreateScope();
        await Repository(scope).UpsertTargetVersionAsync(version, CancellationToken.None);
    }

    private async Task UpsertSettingsAsync(HydrationSettings settings)
    {
        await using var scope = fixture.CreateScope();
        await Repository(scope).UpsertSettingsAsync(settings, CancellationToken.None);
    }

    private static Task<List<string>> PrimaryKey(DatabaseFacade database, string table) =>
        Strings(database,
            $"""
            SELECT conname::text || ' (' || string_agg(attname::text, ', ' ORDER BY array_position(conkey, attnum)) || ')' AS "Value"
            FROM pg_constraint JOIN pg_attribute ON attrelid = conrelid AND attnum = ANY (conkey)
            WHERE conrelid = '{table}'::regclass AND contype = 'p' GROUP BY conname
            """);

    private static HydrationIntake Intake(Guid userId, int amount, DateOnly date, int hour, int minute, int offset = 0) =>
        HydrationIntake.Create(userId, amount, date, new TimeOnly(hour, minute), offset, Now);

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now.AddDays(-30));
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private async Task<HydrationIntake> AddAsync(HydrationIntake intake)
    {
        await using var scope = fixture.CreateScope();
        await Repository(scope).AddAsync(intake, CancellationToken.None);

        return intake;
    }

    private async Task<HydrationIntake?> GetAsync(Guid userId, Guid id)
    {
        await using var scope = fixture.CreateScope();

        return await Repository(scope).GetAsync(userId, id, CancellationToken.None);
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlRawAsync(sql);
    }

    private static IHydrationRepository Repository(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IHydrationRepository>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static Task<List<string>> Tables(DatabaseFacade database) =>
        Strings(database, "SELECT tablename::text AS \"Value\" FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename COLLATE \"C\"");

    private static Task<List<string>> Columns(DatabaseFacade database, string table) =>
        Strings(database,
            $"""
            SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
            FROM pg_attribute WHERE attrelid = '{table}'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum
            """);

    private static async Task<List<string>> Constraints(DatabaseFacade database, string table) =>
        (await Strings(database,
            $"""
            SELECT conname::text || CASE WHEN contype = 'f' THEN ' ' || confdeltype::text ELSE '' END AS "Value" FROM pg_constraint
            WHERE conrelid = '{table}'::regclass AND contype IN ('c', 'f')
            """)).Order(StringComparer.Ordinal).ToList();

    private static Task<List<string>> Strings(DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
