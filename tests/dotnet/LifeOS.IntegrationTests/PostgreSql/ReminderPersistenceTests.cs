using LifeOS.Application.Reminders;
using LifeOS.Domain.Reminders;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// REM-001 §10 (R2) against real PostgreSQL: the AddReminders schema and its checks, the repository
// (round trip, list filter, isolation), user deletion and Down/Up.
//
// The database is shared by the whole PostgreSQL collection, so every assertion is scoped to this
// test's own users.
[Collection(PostgreSqlCollection.Name)]
public class ReminderPersistenceTests(PostgreSqlFixture fixture)
{
    private static readonly DateTime LocalNow = new(2026, 10, 14, 9, 0, 0);
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 7, 0, 0, TimeSpan.Zero);

    private static readonly ReminderSchedule Daily0830 = ReminderSchedule.Daily(new TimeOnly(8, 30));
    private static readonly ReminderSchedule MonThu = ReminderSchedule.Weekly([DayOfWeek.Monday, DayOfWeek.Thursday], new TimeOnly(19, 0));
    private static readonly ReminderSchedule Dentist = ReminderSchedule.Once(new DateOnly(2026, 10, 20), new TimeOnly(10, 0));

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesTheRemindersTable()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddReminders", StringComparison.Ordinal));
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "id uuid NOT NULL", "user_id uuid NOT NULL", "kind character varying(16) NOT NULL", "title character varying(80) NULL",
                "note character varying(500) NULL", "schedule_kind character varying(8) NOT NULL", "once_date date NULL", "weekdays smallint NULL",
                "local_time time without time zone NOT NULL", "status character varying(16) NOT NULL",
                "next_due_local timestamp without time zone NULL", "snooze_until_utc timestamp with time zone NULL",
                "last_triggered_at_utc timestamp with time zone NULL", "last_outcome character varying(16) NULL",
                "created_at_utc timestamp with time zone NOT NULL", "updated_at_utc timestamp with time zone NOT NULL",
                // R5 AddConditionalReminders.
                "habit_id uuid NULL"
            ],
            await Strings(database,
                """
                SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
                FROM pg_attribute WHERE attrelid = 'reminders'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum
                """));

        // The user FK restricts (r): module data is never deleted silently with a user. The habit FK (R5)
        // cascades (c): deleting a habit deletes its nudges.
        Assert.Equal(
            ["FK_reminders_habits c", "FK_reminders_users_user_id r", "ck_reminders_kind", "ck_reminders_last_outcome", "ck_reminders_next_due",
             "ck_reminders_note", "ck_reminders_schedule", "ck_reminders_shape", "ck_reminders_snooze", "ck_reminders_status", "ck_reminders_title"],
            (await Strings(database,
                """
                SELECT conname::text || CASE WHEN contype = 'f' THEN ' ' || confdeltype::text ELSE '' END AS "Value" FROM pg_constraint
                WHERE conrelid = 'reminders'::regclass AND contype IN ('c', 'f')
                """)).Order(StringComparer.Ordinal));

        Assert.Equal(
            [
                "CREATE UNIQUE INDEX \"PK_reminders\" ON public.reminders USING btree (id)",
                "CREATE INDEX ix_reminders_due ON public.reminders USING btree (next_due_local) WHERE ((status)::text = 'Active'::text)",
                "CREATE INDEX ix_reminders_habit_user ON public.reminders USING btree (habit_id, user_id)",
                "CREATE INDEX ix_reminders_snooze ON public.reminders USING btree (snooze_until_utc) WHERE (snooze_until_utc IS NOT NULL)",
                "CREATE INDEX ix_reminders_user ON public.reminders USING btree (user_id)",
                "CREATE UNIQUE INDEX ux_reminders_habit ON public.reminders USING btree (habit_id) WHERE ((kind)::text = 'HabitIncomplete'::text)",
                "CREATE UNIQUE INDEX ux_reminders_user_hydration ON public.reminders USING btree (user_id) WHERE ((kind)::text = 'HydrationGoal'::text)"
            ],
            await Strings(database, "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'reminders' ORDER BY indexname COLLATE \"C\""));
    }

    // Down drops the reminders table and nothing else; Up recreates it. Always migrated back up.
    [Fact]
    public async Task Migration_DownAndUp_TouchOnlyTheRemindersTable()
    {
        var user = await NewUserAsync();
        await AddAsync(Create(user.Id));

        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;
        var migrator = database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        var migrations = database.GetMigrations().ToList();
        var previous = migrations[migrations.FindIndex(id => id.EndsWith("_AddReminders", StringComparison.Ordinal)) - 1];
        var tablesBefore = await Tables(database);

        try
        {
            await migrator.MigrateAsync(previous);

            // HYD-001's later hydration tables go too.
            Assert.Equal(tablesBefore.Except(["hydration_intakes", "hydration_settings", "hydration_target_versions", "reminders"]),
                await Tables(database));
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
        var id = (await AddAsync(Create(user.Id))).Id;

        await Violates("ck_reminders_kind", $"UPDATE reminders SET kind = 'Hourly' WHERE id = '{id}'");
        // R5: a conditional kind has no title (and a habit reminder has a habit).
        await Violates("ck_reminders_shape", $"UPDATE reminders SET kind = 'HydrationGoal' WHERE id = '{id}'");
        await Violates("ck_reminders_title", $"UPDATE reminders SET title = '  ' WHERE id = '{id}'");
        await Violates("ck_reminders_note", $"UPDATE reminders SET note = '' WHERE id = '{id}'");
        await Violates("ck_reminders_schedule", $"UPDATE reminders SET schedule_kind = 'Hourly' WHERE id = '{id}'");
        await Violates("ck_reminders_schedule", $"UPDATE reminders SET schedule_kind = 'Once' WHERE id = '{id}'");
        await Violates("ck_reminders_schedule", $"UPDATE reminders SET once_date = '2026-10-20' WHERE id = '{id}'");
        await Violates("ck_reminders_schedule", $"UPDATE reminders SET schedule_kind = 'Weekly' WHERE id = '{id}'");
        await Violates("ck_reminders_schedule", $"UPDATE reminders SET schedule_kind = 'Weekly', weekdays = 128 WHERE id = '{id}'");
        await Violates("ck_reminders_schedule", $"UPDATE reminders SET schedule_kind = 'Weekly', weekdays = 0 WHERE id = '{id}'");
        await Violates("ck_reminders_schedule", $"UPDATE reminders SET local_time = '08:30:15' WHERE id = '{id}'");
        await Violates("ck_reminders_schedule", $"UPDATE reminders SET schedule_kind = 'Once', once_date = 'infinity' WHERE id = '{id}'");
        await Violates("ck_reminders_status", $"UPDATE reminders SET status = 'Archived' WHERE id = '{id}'");
        await Violates("ck_reminders_next_due", $"UPDATE reminders SET next_due_local = NULL WHERE id = '{id}'");
        await Violates("ck_reminders_next_due", $"UPDATE reminders SET status = 'Completed' WHERE id = '{id}'");
        await Violates("ck_reminders_next_due", $"UPDATE reminders SET next_due_local = '2026-10-15 08:30:30' WHERE id = '{id}'");
        await Violates("ck_reminders_snooze", $"UPDATE reminders SET snooze_until_utc = '2026-10-14 07:15:30+00' WHERE id = '{id}'");
        await Violates("ck_reminders_last_outcome", $"UPDATE reminders SET last_outcome = 'Skipped' WHERE id = '{id}'");

        // A Paused reminder may keep or drop its next occurrence; a Completed one has none.
        await Execute($"UPDATE reminders SET status = 'Paused', next_due_local = NULL WHERE id = '{id}'");
        await Execute($"UPDATE reminders SET status = 'Completed' WHERE id = '{id}'");
    }

    // ---- Repository ----

    [Fact]
    public async Task Reminders_RoundTrip_EveryField()
    {
        var user = await NewUserAsync();
        var weekly = Reminder.Create(user.Id, "Water the plants", "Balcony", MonThu, LocalNow, Now.AddTicks(1234567));
        weekly.Snooze(60, Now);
        weekly.Advance(new DateTime(2026, 10, 15, 19, 0, 0), ReminderOutcome.Notified, Now.AddTicks(7654321));
        await AddAsync(weekly);
        var once = await AddAsync(Reminder.Create(user.Id, "Call the dentist", null, Dentist, LocalNow, Now));

        await using var scope = fixture.CreateScope();
        var stored = (await Repository(scope).GetAsync(user.Id, weekly.Id, default))!;

        Assert.Equal((weekly.Title, weekly.Note, weekly.Kind, weekly.Status), (stored.Title, stored.Note, stored.Kind, stored.Status));
        Assert.Equal(MonThu, stored.Schedule);
        Assert.Equal((weekly.NextDueLocal, weekly.SnoozeUntilUtc, weekly.LastTriggeredAtUtc, weekly.LastOutcome),
            (stored.NextDueLocal, stored.SnoozeUntilUtc, stored.LastTriggeredAtUtc, stored.LastOutcome));
        Assert.Equal((weekly.CreatedAtUtc, weekly.UpdatedAtUtc), (stored.CreatedAtUtc, stored.UpdatedAtUtc));
        Assert.Equal(DateTimeKind.Unspecified, stored.NextDueLocal!.Value.Kind);

        Assert.Equal(Dentist, (await Repository(scope).GetAsync(user.Id, once.Id, default))!.Schedule);
    }

    [Fact]
    public async Task Save_PersistsChanges_AndADeletedReminderIsNotSaved()
    {
        var user = await NewUserAsync();
        var reminder = await AddAsync(Create(user.Id));

        await using (var scope = fixture.CreateScope())
        {
            var stored = (await Repository(scope).GetAsync(user.Id, reminder.Id, default))!;
            stored.Update("Vitamin D", null, Dentist, LocalNow, Now.AddMinutes(1));
            stored.Pause(Now.AddMinutes(2));
            Assert.True(await Repository(scope).SaveAsync(stored, default));
        }

        await using (var scope = fixture.CreateScope())
        {
            var stored = (await Repository(scope).GetAsync(user.Id, reminder.Id, default))!;
            Assert.Equal(("Vitamin D", Dentist, ReminderStatus.Paused), (stored.Title, stored.Schedule, stored.Status));

            await Execute($"DELETE FROM reminders WHERE id = '{reminder.Id}'");
            stored.Resume(LocalNow, Now);
            Assert.False(await Repository(scope).SaveAsync(stored, default));
        }
    }

    [Fact]
    public async Task List_Count_AndDelete_AreOwnerScoped()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();
        var active = await AddAsync(Create(user.Id, "Active"));
        var completed = Reminder.Create(user.Id, "Recently completed", null, Dentist, LocalNow, Now);
        completed.Advance(new DateTime(2026, 10, 20, 10, 0, 0), ReminderOutcome.Notified, Now);
        await AddAsync(completed);
        var old = Reminder.Create(user.Id, "Old", null, Dentist, LocalNow, Now);
        old.Advance(new DateTime(2026, 10, 20, 10, 0, 0), ReminderOutcome.Notified, Now.AddDays(-40));
        await AddAsync(old);
        var foreign = await AddAsync(Create(other.Id, "Foreign"));

        await using var scope = fixture.CreateScope();
        var repository = Repository(scope);

        Assert.Equal(["Active", "Recently completed"], (await repository.GetAllAsync(user.Id, Now.AddDays(-30), default)).Select(r => r.Title));
        Assert.Equal(1, await repository.CountOpenAsync(user.Id, default));
        Assert.Null(await repository.GetAsync(user.Id, foreign.Id, default));
        Assert.False(await repository.DeleteAsync(user.Id, foreign.Id, default));
        Assert.NotNull(await repository.GetAsync(other.Id, foreign.Id, default));

        Assert.True(await repository.DeleteAsync(user.Id, active.Id, default));
        Assert.False(await repository.DeleteAsync(user.Id, active.Id, default));
    }

    [Fact]
    public async Task AUserWithReminders_CannotBeDeleted()
    {
        var user = await NewUserAsync();
        await AddAsync(Create(user.Id));

        await PostgresAssert.DeleteBlockedAsync("FK_reminders_users_user_id", () => Execute($"DELETE FROM users WHERE id = '{user.Id}'"));
    }

    // ---- Helpers ----

    private static Reminder Create(Guid userId, string title = "Take vitamins") => Reminder.Create(userId, title, null, Daily0830, LocalNow, Now);

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Now.AddDays(-400));
        await PostgresAssert.InsertAsync(fixture, user);
        return user;
    }

    private async Task<Reminder> AddAsync(Reminder reminder)
    {
        await using var scope = fixture.CreateScope();
        await Repository(scope).AddAsync(reminder, default);
        return reminder;
    }

    private Task Violates(string constraint, string sql) =>
        PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, constraint, () => Execute(sql));

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlRawAsync(sql);
    }

    private static IReminderRepository Repository(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IReminderRepository>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static Task<List<string>> Strings(DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
