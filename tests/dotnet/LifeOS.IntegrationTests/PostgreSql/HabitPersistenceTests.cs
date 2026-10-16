using LifeOS.Application.Habits;
using LifeOS.Domain.Habits;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// HAB-001 §9 against real PostgreSQL: the AddHabits schema, its checks and composite ownership keys,
// the repository (versions round trip, upsert race, isolation), cascades and user deletion.
//
// The database is shared by the whole PostgreSQL collection, so every assertion is scoped to this
// test's own users.
[Collection(PostgreSqlCollection.Name)]
public class HabitPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateOnly Today = new(2026, 10, 14);
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static readonly HabitFrequency MonWedFri =
        HabitFrequency.OnWeekdays(WeekdaySet.Of(DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday));

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesTheThreeHabitTables()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddHabits", StringComparison.Ordinal));
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "id uuid NOT NULL", "user_id uuid NOT NULL", "name character varying(80) NOT NULL",
                "description character varying(500) NULL", "completion_source character varying(16) NOT NULL",
                "starts_on date NOT NULL", "status character varying(16) NOT NULL",
                "created_at_utc timestamp with time zone NOT NULL", "updated_at_utc timestamp with time zone NOT NULL",
                "archived_at_utc timestamp with time zone NULL"
            ],
            await Columns(database, "habits"));
        Assert.Equal(
            [
                "habit_id uuid NOT NULL", "effective_from date NOT NULL", "user_id uuid NOT NULL",
                "frequency character varying(16) NOT NULL", "weekdays smallint NULL", "times_per_week smallint NULL"
            ],
            await Columns(database, "habit_schedule_versions"));
        Assert.Equal(
            [
                "habit_id uuid NOT NULL", "date date NOT NULL", "user_id uuid NOT NULL", "status character varying(16) NOT NULL",
                "note character varying(280) NULL", "created_at_utc timestamp with time zone NOT NULL",
                "updated_at_utc timestamp with time zone NOT NULL"
            ],
            await Columns(database, "habit_check_ins"));

        // The user FK restricts (r); versions and check-ins cascade (c) with their habit, by (id, user_id).
        Assert.Equal(
            ["FK_habits_users_user_id r", "ck_habits_archived", "ck_habits_completion_source", "ck_habits_description", "ck_habits_name",
             "ck_habits_starts_on", "ck_habits_status"],
            await Constraints(database, "habits"));
        Assert.Equal(
            ["FK_habit_schedule_versions_habits c", "ck_habit_schedule_versions_effective_from", "ck_habit_schedule_versions_frequency",
             "ck_habit_schedule_versions_shape"],
            await Constraints(database, "habit_schedule_versions"));
        Assert.Equal(
            ["FK_habit_check_ins_habits c", "ck_habit_check_ins_date", "ck_habit_check_ins_note", "ck_habit_check_ins_status"],
            await Constraints(database, "habit_check_ins"));

        Assert.Equal(
            ["PK_habits", "ix_habits_user_status", "ux_habits_id_user", "ux_habits_user_active_hydration"],
            await Strings(database, "SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'habits' ORDER BY indexname COLLATE \"C\""));
        Assert.Equal(
            ["PK_habit_check_ins", "ix_habit_check_ins_habit_user", "ix_habit_check_ins_user_date"],
            await Strings(database, "SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename = 'habit_check_ins' ORDER BY indexname COLLATE \"C\""));

        var hydration = (await Strings(database,
            "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'ux_habits_user_active_hydration'")).Single();
        Assert.Contains("UNIQUE", hydration);
        Assert.Contains("'Hydration'", hydration);
        Assert.Contains("'Active'", hydration);

        // Composite foreign keys reference (id, user_id), never id alone.
        Assert.Equal(
            ["FK_habit_check_ins_habits 2", "FK_habit_schedule_versions_habits 2"],
            await Strings(database,
                """
                SELECT conname || ' ' || array_length(conkey, 1) AS "Value" FROM pg_constraint
                WHERE confrelid = 'habits'::regclass AND contype = 'f' ORDER BY conname
                """));

        // No water amount anywhere in Habits (G-3).
        Assert.Empty(await Strings(database,
            "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name LIKE 'habit%' AND column_name LIKE '%ml%'"));
    }

    // Down drops the three habit tables (and later migrations' tables) and nothing else; Up recreates
    // them. The collection runs one test at a time, so the shared schema is complete again before the
    // next test.
    [Fact]
    public async Task Migration_DownAndUp_TouchOnlyTheHabitTables()
    {
        var user = await NewUserAsync();
        await AddAsync(Create(user.Id, HabitFrequency.Daily, Today));

        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;
        var migrator = database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        var migrations = database.GetMigrations().ToList();
        var addHabits = migrations.Single(id => id.EndsWith("_AddHabits", StringComparison.Ordinal));
        var previous = migrations[migrations.IndexOf(addHabits) - 1];
        var tablesBefore = await Tables(database);

        try
        {
            await migrator.MigrateAsync(previous);

            // REM-001's and HYD-001's tables come from later migrations, so they are rolled back too.
            Assert.Equal(tablesBefore.Except(["habit_check_ins", "habit_schedule_versions", "habits", "hydration_intakes", "hydration_settings",
                "hydration_target_versions", "reminders"]), await Tables(database));
            Assert.True(await Db(scope).Users.AnyAsync(stored => stored.Id == user.Id));
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        Assert.Equal(tablesBefore, await Tables(database));
        Assert.Empty(await database.GetPendingMigrationsAsync());

        static Task<List<string>> Tables(DatabaseFacade database) =>
            Strings(database, "SELECT tablename::text AS \"Value\" FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename COLLATE \"C\"");
    }

    [Fact]
    public async Task InvalidRows_AreRejectedByTheDatabase()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();
        var habit = await AddAsync(Create(user.Id, MonWedFri, Today.AddDays(-7)));
        await UpsertAsync(habit.CheckIn(Today, HabitCheckInStatus.Done, null, Today, Now));
        var id = habit.Id;

        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_habits_name", () => Execute($"UPDATE habits SET name = '  ' WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_habits_description", () => Execute($"UPDATE habits SET description = '' WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_habits_completion_source", () => Execute($"UPDATE habits SET completion_source = 'Gym' WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_habits_status", () => Execute($"UPDATE habits SET status = 'Paused' WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_habits_archived", () => Execute($"UPDATE habits SET status = 'Archived' WHERE id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_habits_starts_on", () => Execute($"UPDATE habits SET starts_on = '-infinity' WHERE id = '{id}'"));

        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_habit_schedule_versions_shape",
            () => Execute($"UPDATE habit_schedule_versions SET weekdays = NULL WHERE habit_id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_habit_schedule_versions_shape",
            () => Execute($"UPDATE habit_schedule_versions SET weekdays = 128 WHERE habit_id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_habit_schedule_versions_shape",
            () => Execute($"UPDATE habit_schedule_versions SET frequency = 'TimesPerWeek', weekdays = NULL, times_per_week = 8 WHERE habit_id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_habit_schedule_versions_frequency",
            () => Execute($"UPDATE habit_schedule_versions SET frequency = 'Monthly' WHERE habit_id = '{id}'"));

        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_habit_check_ins_status",
            () => Execute($"UPDATE habit_check_ins SET status = 'Missed' WHERE habit_id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_habit_check_ins_note",
            () => Execute($"UPDATE habit_check_ins SET note = '   ' WHERE habit_id = '{id}'"));
        // Longer notes never reach the check: the column is varchar(280).
        await PostgresAssert.ViolatesAsync("22001", null!,
            () => Execute($"UPDATE habit_check_ins SET note = repeat('n', 281) WHERE habit_id = '{id}'"));

        // A check-in or version can't point at the habit with another user's id (composite key).
        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, "FK_habit_check_ins_habits",
            () => Execute($"UPDATE habit_check_ins SET user_id = '{other.Id}' WHERE habit_id = '{id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, "FK_habit_schedule_versions_habits",
            () => Execute($"UPDATE habit_schedule_versions SET user_id = '{other.Id}' WHERE habit_id = '{id}'"));
    }

    [Fact]
    public async Task AtMostOneActiveHydrationHabitPerUser()
    {
        var user = await NewUserAsync();
        var first = await AddAsync(Create(user.Id, HabitFrequency.Daily, Today, HabitCompletionSource.Hydration));

        await PostgresAssert.ViolatesAsync(PostgresAssert.UniqueViolation, "ux_habits_user_active_hydration",
            () => AddAsync(Create(user.Id, HabitFrequency.Daily, Today, HabitCompletionSource.Hydration)));

        // An archived one does not count; manual habits are unlimited by this index.
        await Execute($"UPDATE habits SET status = 'Archived', archived_at_utc = now() WHERE id = '{first.Id}'");
        await AddAsync(Create(user.Id, HabitFrequency.Daily, Today, HabitCompletionSource.Hydration));
        await AddAsync(Create(user.Id, HabitFrequency.Daily, Today));
        await AddAsync(Create(user.Id, HabitFrequency.Daily, Today));
    }

    // B4: the repository turns the index's rejection of a concurrent second water habit (create or restore)
    // into the stable conflict code, keeping PostgreSQL's error as the inner exception.
    [Fact]
    public async Task SecondActiveWaterHabit_IsAConflict_OnAddAndOnRestore()
    {
        var user = await NewUserAsync();
        var first = await AddAsync(Create(user.Id, HabitFrequency.Daily, Today, HabitCompletionSource.Hydration));

        await using (var scope = fixture.CreateScope())
        {
            var repository = Repository(scope);
            Assert.True(await repository.HasActiveHydrationHabitAsync(user.Id, null, CancellationToken.None));
            Assert.False(await repository.HasActiveHydrationHabitAsync(user.Id, first.Id, CancellationToken.None));

            var conflict = await Assert.ThrowsAsync<HabitConflictException>(() =>
                repository.AddAsync(Create(user.Id, HabitFrequency.Daily, Today, HabitCompletionSource.Hydration), CancellationToken.None));
            Assert.Equal(HabitConflictException.HydrationHabitExists, conflict.Code);
        }

        await Execute($"UPDATE habits SET status = 'Archived', archived_at_utc = now() WHERE id = '{first.Id}'");
        await AddAsync(Create(user.Id, HabitFrequency.Daily, Today, HabitCompletionSource.Hydration));

        await using (var scope = fixture.CreateScope())
        {
            var repository = Repository(scope);
            var archived = (await repository.GetAsync(user.Id, first.Id, CancellationToken.None))!;
            archived.Restore(Today.AddDays(1), null, Now.AddDays(1));

            var conflict = await Assert.ThrowsAsync<HabitConflictException>(() => repository.SaveAsync(archived, CancellationToken.None));
            Assert.Equal(HabitConflictException.HydrationHabitExists, conflict.Code);
        }

        Assert.Equal(HabitStatus.Archived, (await GetAsync(user.Id, first.Id))!.Status);
    }

    // ---- Repository ----

    [Fact]
    public async Task Versions_RoundTrip_ThroughChangeArchiveRestoreAndMerge()
    {
        var user = await NewUserAsync();
        var habit = await AddAsync(Create(user.Id, MonWedFri, Today.AddDays(-20)));

        await ChangeAsync(user.Id, habit.Id, stored => stored.ChangeFrequency(HabitFrequency.PerWeek(3), Today.AddDays(-10), Now));
        await ChangeAsync(user.Id, habit.Id, stored => stored.Archive(Today.AddDays(-5), Now));
        await ChangeAsync(user.Id, habit.Id, stored => stored.Restore(Today, null, Now));

        var restored = (await GetAsync(user.Id, habit.Id))!;
        Assert.Equal(
            [
                (Today.AddDays(-20), HabitFrequencyKind.Weekdays, (int?)21, (int?)null),
                (Today.AddDays(-10), HabitFrequencyKind.TimesPerWeek, null, 3),
                (Today.AddDays(-5), HabitFrequencyKind.Inactive, null, null),
                (Today, HabitFrequencyKind.TimesPerWeek, null, 3)
            ],
            restored.Schedule.Select(version => (version.EffectiveFrom, version.Kind, version.WeekdaysMask, version.TimesPerWeek)));
        Assert.Equal((HabitStatus.Active, (DateTimeOffset?)null), (restored.Status, restored.ArchivedAtUtc));

        // Changing today's version replaces it in place; going back to the previous frequency removes it.
        await ChangeAsync(user.Id, habit.Id, stored => stored.ChangeFrequency(HabitFrequency.Daily, Today, Now));
        Assert.Equal(HabitFrequencyKind.Daily, (await GetAsync(user.Id, habit.Id))!.Schedule[^1].Kind);
        await ChangeAsync(user.Id, habit.Id, stored => stored.Archive(Today, Now));
        Assert.Equal(
            [HabitFrequencyKind.Weekdays, HabitFrequencyKind.TimesPerWeek, HabitFrequencyKind.Inactive],
            (await GetAsync(user.Id, habit.Id))!.Schedule.Select(version => version.Kind));
    }

    [Fact]
    public async Task RoundTrip_KeepsEveryField_AndListsInCreationOrder()
    {
        var user = await NewUserAsync();
        var habit = Habit.Create(user.Id, "Stretch", "Ten minutes", HabitCompletionSource.Manual, HabitFrequency.PerWeek(2),
            Today.AddDays(-1), Today, Now.AddTicks(1234567));
        await AddAsync(habit);
        var later = await AddAsync(Habit.Create(user.Id, "Walk", null, HabitCompletionSource.Manual, HabitFrequency.Daily, Today, Today, Now.AddHours(1)));

        var stored = (await GetAsync(user.Id, habit.Id))!;
        Assert.Equal(
            (habit.Name, habit.Description, habit.CompletionSource, habit.StartsOn, habit.Status, habit.CreatedAtUtc, habit.UpdatedAtUtc),
            (stored.Name, stored.Description, stored.CompletionSource, stored.StartsOn, stored.Status, stored.CreatedAtUtc, stored.UpdatedAtUtc));
        Assert.Equal(HabitFrequency.PerWeek(2), stored.CurrentFrequency);

        await using var scope = fixture.CreateScope();
        var repository = Repository(scope);
        Assert.Equal([habit.Id, later.Id], (await repository.GetAllAsync(user.Id, CancellationToken.None)).Select(item => item.Id));
        Assert.Equal(2, await repository.CountActiveAsync(user.Id, CancellationToken.None));
    }

    [Fact]
    public async Task CheckIns_UpsertReplaces_RemoveIsIdempotent()
    {
        var user = await NewUserAsync();
        var habit = await AddAsync(Create(user.Id, HabitFrequency.Daily, Today.AddDays(-3)));

        Assert.True(await UpsertAsync(habit.CheckIn(Today, HabitCheckInStatus.Done, null, Today, Now)));
        Assert.True(await UpsertAsync(habit.CheckIn(Today, HabitCheckInStatus.Excused, "Sick", Today, Now.AddMinutes(5))));
        Assert.True(await UpsertAsync(habit.CheckIn(Today.AddDays(-2), HabitCheckInStatus.Done, null, Today, Now)));

        var checkIns = await CheckInsAsync(user.Id, habit.Id, Today.AddDays(-3), Today);
        Assert.Equal(
            [(Today.AddDays(-2), HabitCheckInStatus.Done, (string?)null), (Today, HabitCheckInStatus.Excused, "Sick")],
            checkIns.Select(checkIn => (checkIn.Date, checkIn.Status, checkIn.Note)));
        // The first save's creation time is kept; the update time moves.
        Assert.Equal((Now, Now.AddMinutes(5)), (checkIns[1].CreatedAtUtc, checkIns[1].UpdatedAtUtc));

        Assert.Equal([Today.AddDays(-2)], (await CheckInsAsync(user.Id, habit.Id, Today.AddDays(-3), Today.AddDays(-1))).Select(c => c.Date));

        await using var scope = fixture.CreateScope();
        var repository = Repository(scope);
        Assert.True(await repository.RemoveCheckInAsync(user.Id, habit.Id, Today, CancellationToken.None));
        Assert.False(await repository.RemoveCheckInAsync(user.Id, habit.Id, Today, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentUpserts_OfOneDate_LeaveOneRow()
    {
        var user = await NewUserAsync();
        var habit = await AddAsync(Create(user.Id, HabitFrequency.Daily, Today.AddDays(-3)));

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => UpsertAsync(
            habit.CheckIn(Today, index % 2 == 0 ? HabitCheckInStatus.Done : HabitCheckInStatus.Excused, $"Note {index}", Today, Now))));

        Assert.All(results, Assert.True);
        Assert.Single(await CheckInsAsync(user.Id, habit.Id, Today, Today));
    }

    [Fact]
    public async Task EveryOperation_IsScopedToTheOwner()
    {
        var owner = await NewUserAsync();
        var other = await NewUserAsync();
        var habit = await AddAsync(Create(owner.Id, HabitFrequency.Daily, Today.AddDays(-3)));
        await UpsertAsync(habit.CheckIn(Today, HabitCheckInStatus.Done, null, Today, Now));

        await using var scope = fixture.CreateScope();
        var repository = Repository(scope);

        Assert.Null(await repository.GetAsync(other.Id, habit.Id, CancellationToken.None));
        Assert.Empty(await repository.GetAllAsync(other.Id, CancellationToken.None));
        Assert.Equal(0, await repository.CountActiveAsync(other.Id, CancellationToken.None));
        Assert.Empty(await repository.GetCheckInsAsync(other.Id, [habit.Id], Today.AddDays(-3), Today, CancellationToken.None));
        Assert.False(await repository.RemoveCheckInAsync(other.Id, habit.Id, Today, CancellationToken.None));
        Assert.False(await repository.DeleteAsync(other.Id, habit.Id, CancellationToken.None));

        // A check-in carrying another user's id is never written onto the owner's habit.
        var forged = typeof(HabitCheckIn).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single(constructor => constructor.GetParameters().Length == 6)
            .Invoke([habit.Id, other.Id, Today.AddDays(-1), HabitCheckInStatus.Done, null, Now]);
        Assert.False(await repository.UpsertCheckInAsync((HabitCheckIn)forged, CancellationToken.None));

        Assert.Single(await CheckInsAsync(owner.Id, habit.Id, Today.AddDays(-3), Today));
        Assert.NotNull(await GetAsync(owner.Id, habit.Id));
    }

    [Fact]
    public async Task Delete_CascadesVersionsAndCheckIns_OnlyForThatHabit()
    {
        var user = await NewUserAsync();
        var habit = await AddAsync(Create(user.Id, HabitFrequency.Daily, Today.AddDays(-3)));
        var kept = await AddAsync(Create(user.Id, HabitFrequency.Daily, Today.AddDays(-3)));
        await ChangeAsync(user.Id, habit.Id, stored => stored.ChangeFrequency(MonWedFri, Today, Now));
        await UpsertAsync(habit.CheckIn(Today, HabitCheckInStatus.Done, null, Today, Now));
        await UpsertAsync(kept.CheckIn(Today, HabitCheckInStatus.Done, null, Today, Now));

        await using (var scope = fixture.CreateScope())
        {
            Assert.True(await Repository(scope).DeleteAsync(user.Id, habit.Id, CancellationToken.None));
        }

        await using var check = fixture.CreateScope();
        var db = Db(check);
        Assert.Equal(0, await db.Set<HabitScheduleVersion>().CountAsync(version => version.HabitId == habit.Id));
        Assert.Equal(0, await db.HabitCheckIns.CountAsync(checkIn => checkIn.HabitId == habit.Id));
        Assert.Equal(1, await db.Set<HabitScheduleVersion>().CountAsync(version => version.HabitId == kept.Id));
        Assert.Equal(1, await db.HabitCheckIns.CountAsync(checkIn => checkIn.HabitId == kept.Id));

        // Saving a habit deleted meanwhile reports it as gone.
        var stale = await AddAsync(Create(user.Id, HabitFrequency.Daily, Today.AddDays(-3)));
        await using var editing = fixture.CreateScope();
        var loaded = (await Repository(editing).GetAsync(user.Id, stale.Id, CancellationToken.None))!;
        await using (var scope = fixture.CreateScope())
        {
            await Repository(scope).DeleteAsync(user.Id, stale.Id, CancellationToken.None);
        }

        loaded.UpdateDetails("Renamed", null, Now);
        Assert.False(await Repository(editing).SaveAsync(loaded, CancellationToken.None));
        Assert.False(await UpsertAsync(stale.CheckIn(Today, HabitCheckInStatus.Done, null, Today, Now)));
    }

    [Fact]
    public async Task UserWithHabits_CannotBeDeleted()
    {
        var user = await NewUserAsync();
        await AddAsync(Create(user.Id, HabitFrequency.Daily, Today));

        await PostgresAssert.DeleteBlockedAsync("FK_habits_users_user_id", () => Execute($"DELETE FROM users WHERE id = '{user.Id}'"));
    }

    // ---- Helpers ----

    private static Habit Create(Guid userId, HabitFrequency frequency, DateOnly startsOn, HabitCompletionSource source = HabitCompletionSource.Manual) =>
        Habit.Create(userId, "Read 20 pages", null, source, frequency, startsOn, Today, Now);

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now.AddDays(-30));
        await PostgresAssert.InsertAsync(fixture, user);

        return user;
    }

    private async Task<Habit> AddAsync(Habit habit)
    {
        await using var scope = fixture.CreateScope();
        await Repository(scope).AddAsync(habit, CancellationToken.None);

        return habit;
    }

    private async Task<Habit?> GetAsync(Guid userId, Guid id)
    {
        await using var scope = fixture.CreateScope();

        return await Repository(scope).GetAsync(userId, id, CancellationToken.None);
    }

    private async Task ChangeAsync(Guid userId, Guid id, Action<Habit> change)
    {
        await using var scope = fixture.CreateScope();
        var repository = Repository(scope);
        var habit = (await repository.GetAsync(userId, id, CancellationToken.None))!;
        change(habit);
        Assert.True(await repository.SaveAsync(habit, CancellationToken.None));
    }

    private async Task<bool> UpsertAsync(HabitCheckIn checkIn)
    {
        await using var scope = fixture.CreateScope();

        return await Repository(scope).UpsertCheckInAsync(checkIn, CancellationToken.None);
    }

    private async Task<IReadOnlyList<HabitCheckIn>> CheckInsAsync(Guid userId, Guid habitId, DateOnly from, DateOnly to)
    {
        await using var scope = fixture.CreateScope();

        return await Repository(scope).GetCheckInsAsync(userId, [habitId], from, to, CancellationToken.None);
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlRawAsync(sql);
    }

    private static IHabitRepository Repository(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IHabitRepository>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

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
