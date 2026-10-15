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

            Assert.Equal(tablesBefore.Except(Hydration(tablesBefore)), await Tables(database));
            Assert.True(await Db(scope).Users.AnyAsync(stored => stored.Id == user.Id));
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        Assert.Equal(tablesBefore, await Tables(database));
        Assert.Empty(await database.GetPendingMigrationsAsync());

        // Rolling back AddHydrationIntakes also rolls back every later migration (H2's tables included).
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
    }

    [Fact]
    public async Task DeletingAUserWithIntakes_IsRestricted()
    {
        var user = await NewUserAsync();
        await AddAsync(Intake(user.Id, 250, Today, 8, 0));

        await PostgresAssert.DeleteBlockedAsync("FK_hydration_intakes_users_user_id", () => Execute($"DELETE FROM users WHERE id = '{user.Id}'"));
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

    // ---- Helpers ----

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
