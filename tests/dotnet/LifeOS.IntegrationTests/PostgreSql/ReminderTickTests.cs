using LifeOS.Application.Automation;
using LifeOS.Application.Notifications;
using LifeOS.Application.Persistence;
using LifeOS.Application.Reminders;
using LifeOS.Domain.Automation;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Reminders;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// REM-001 R3 against real PostgreSQL: the discovery SQL (zone join, preferences, paused), the
// maintenance writes, and ReminderAutomationHandler end to end through the real tick engine, execution
// store, unit of work and outbox: one claim per occurrence (also under concurrent ticks), late ticks,
// retries, final failures, quiet hours, FCM retries and snoozes. Also the AddReminderPreferences column.
//
// The database is shared by the whole PostgreSQL collection, so every assertion is scoped to this test's
// own users, reminders and device tokens. Asia/Tokyo (UTC+9, no DST) is "the user's zone"; the dates
// (Nov 2026) are not used by other reminder tests.
[Collection(PostgreSqlCollection.Name)]
public class ReminderTickTests(PostgreSqlFixture fixture)
{
    private const string Tokyo = "Asia/Tokyo";

    // Created Wed 18 Nov 2026 20:00 Tokyo (11:00Z); a Daily 08:30 is due Thu 19 Nov 08:30 Tokyo = 18 Nov 23:30Z.
    private static readonly DateTimeOffset Created = new(2026, 11, 18, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due = new(2026, 11, 18, 23, 30, 0, TimeSpan.Zero);
    private static readonly DateTime DueLocal = new(2026, 11, 19, 8, 30, 0);

    // ---- AddReminderPreferences ----

    [Fact]
    public async Task Migration_AddsTheUserRemindersSwitch_TrueForExistingRows()
    {
        var user = await NewUserAsync(Tokyo);
        Assert.True(await SetPreferencesAsync(user.Id, userReminders: false));

        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;
        var migrator = database.GetService<IMigrator>();
        var migrations = database.GetMigrations().ToList();
        var previous = migrations[migrations.FindIndex(id => id.EndsWith("_AddReminderPreferences", StringComparison.Ordinal)) - 1];

        Assert.Equal(["user_reminders_enabled boolean NOT NULL DEFAULT true"],
            await Strings(database,
                """
                SELECT column_name || ' ' || data_type || CASE WHEN is_nullable = 'NO' THEN ' NOT NULL' ELSE '' END || ' DEFAULT ' || column_default AS "Value"
                FROM information_schema.columns WHERE table_name = 'notification_preferences' AND column_name = 'user_reminders_enabled'
                """));
        Assert.False((await Preferences(scope).GetAsync(user.Id, default)).UserRemindersEnabled);

        try
        {
            await migrator.MigrateAsync(previous);
            Assert.Empty(await Strings(database,
                "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'notification_preferences' AND column_name = 'user_reminders_enabled'"));
        }
        finally
        {
            await migrator.MigrateAsync();
        }

        // A row saved before the column existed gets the default: personal reminders on.
        await using var after = fixture.CreateScope();
        var saved = await Preferences(after).GetAsync(user.Id, default);
        Assert.Equal((true, true, true), (saved.UserRemindersEnabled, saved.RecurringTransactionRemindersEnabled, saved.PlannedExpenseRemindersEnabled));
    }

    // ---- Discovery SQL ----

    [Fact]
    public async Task Discovery_JoinsEachUsersOwnZone_AndSkipsPausedMutedAndZonelessUsers()
    {
        var tokyo = await NewUserAsync(Tokyo);
        var saoPaulo = await NewUserAsync("America/Sao_Paulo");
        var zoneless = await NewUserAsync();
        var muted = await NewUserAsync(Tokyo);
        Assert.True(await SetPreferencesAsync(muted.Id, userReminders: false));

        var due = await AddAsync(tokyo.Id);
        var paused = await AddAsync(tokyo.Id);
        await ChangeAsync(tokyo.Id, paused.Id, reminder => reminder.Pause(Created));
        // 08:30 in São Paulo (UTC-3) is 11:30Z: not yet.
        var later = await AddAsync(saoPaulo.Id, created: new DateTimeOffset(2026, 11, 18, 23, 0, 0, TimeSpan.Zero), zone: "America/Sao_Paulo");
        await AddAsync(zoneless.Id);
        await AddAsync(muted.Id);

        var mine = new HashSet<Guid> { tokyo.Id, saoPaulo.Id, zoneless.Id, muted.Id };

        Assert.Equal([$"{due.Id:D}:2026-11-19T08:30"], await DueKeysAsync(Due.AddMinutes(1), mine));
        Assert.Empty(await DueKeysAsync(Due.AddMinutes(-1), mine));
        // By then Tokyo's 08:30 is 12 h old (beyond the lateness: skipped as Missed).
        Assert.Equal([$"{later.Id:D}:2026-11-19T08:30"], await DueKeysAsync(new DateTimeOffset(2026, 11, 19, 11, 31, 0, TimeSpan.Zero), mine));
        Assert.Equal((DueLocal.AddDays(1), (ReminderOutcome?)ReminderOutcome.Missed), await NextAsync(due.Id));
        Assert.Equal((DueLocal, (ReminderOutcome?)null), await NextAsync(paused.Id));
    }

    [Fact]
    public async Task Discovery_FollowsAZoneChange_WithoutRewritingTheReminder()
    {
        var user = await NewUserAsync(Tokyo);
        var reminder = await AddAsync(user.Id);
        await Execute($"UPDATE users SET time_zone_id = 'America/Sao_Paulo' WHERE id = '{user.Id}'");

        Assert.Empty(await DueKeysAsync(Due.AddMinutes(1), [user.Id]));
        Assert.Equal([$"{reminder.Id:D}:2026-11-19T08:30"], await DueKeysAsync(new DateTimeOffset(2026, 11, 19, 11, 31, 0, TimeSpan.Zero), [user.Id]));
    }

    [Fact]
    public async Task Maintenance_IsConditional_AndAdvanceNeedsTheCompletionTransaction()
    {
        var user = await NewUserAsync(Tokyo);
        var reminder = await AddAsync(user.Id);

        await using (var scope = fixture.CreateScope())
        {
            var store = Store(scope);

            // A concurrent edit already moved next_due_local: nothing changes.
            await store.SkipMissedAsync([new MissedOccurrence(user.Id, reminder.Id, DueLocal.AddDays(-1), DueLocal)], [], Due, default);
            Assert.Equal((DueLocal, (ReminderOutcome?)null), await NextAsync(reminder.Id));

            await store.SkipMissedAsync([new MissedOccurrence(user.Id, reminder.Id, DueLocal, DueLocal.AddDays(1))], [], Due, default);
            await store.SkipMissedAsync([new MissedOccurrence(user.Id, reminder.Id, DueLocal, DueLocal.AddDays(1))], [], Due, default);
            Assert.Equal((DueLocal.AddDays(1), (ReminderOutcome?)ReminderOutcome.Missed), await NextAsync(reminder.Id));

            // Another user's id never matches.
            await store.SkipMissedAsync([new MissedOccurrence(Guid.CreateVersion7(), reminder.Id, DueLocal.AddDays(1), DueLocal.AddDays(2))], [], Due, default);
            Assert.Equal(DueLocal.AddDays(1), (await NextAsync(reminder.Id)).Next);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.AdvanceAsync(user.Id, ReminderOccurrence.Scheduled(reminder.Id, DueLocal.AddDays(1)), ReminderOutcome.Notified, Due, default));
        }
    }

    // ---- Tick end to end ----

    [Fact]
    public async Task Tick_AtTheLocalTime_NotifiesEachActiveDeviceOnce_WithFixedCopy_AndMovesOn()
    {
        var user = await NewUserAsync(Tokyo);
        var phone = await RegisterDeviceAsync(user.Id);
        var tablet = await RegisterDeviceAsync(user.Id);
        var reminder = await AddAsync(user.Id, title: "Call Dr. Rossi", note: "Ask about the results");
        var sender = new FakePushNotificationSender();

        await TickAsync(Due.AddMinutes(1), sender);     // discovery: execution + one Pending delivery per device
        await TickAsync(Due.AddMinutes(11), sender);    // Phase A sends them; nothing new is discovered

        var execution = Assert.Single(await ExecutionsAsync(user.Id));
        Assert.Equal((ReminderAutomationHandler.Type, $"{reminder.Id:D}:2026-11-19T08:30", AutomationExecutionStatus.Succeeded, Due, Tokyo),
            (execution.AutomationType, execution.OccurrenceKey, execution.Status, execution.ScheduledForUtc, execution.TimeZoneId));
        Assert.Equal(Due + TimeSpan.FromHours(6), execution.ExpiresAtUtc);

        var deliveries = await DeliveriesAsync(user.Id);
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, row => Assert.Equal(
            (NotificationType.UserReminder, "reminder", (Guid?)reminder.Id, NotificationDeliveryStatus.Sent, $"automation:{execution.Id:D}"),
            (row.NotificationType, row.ResourceType, row.ResourceId, row.Status, row.NotificationKey)));
        Assert.Equal(TimeSpan.FromHours(24), deliveries[0].ExpiresAtUtc - deliveries[0].CreatedAtUtc);

        var messages = sender.Sent.Where(sent => sent.Target.Token == phone.PushToken || sent.Target.Token == tablet.PushToken).Select(sent => sent.Message).ToList();
        Assert.Equal(2, messages.Count);
        Assert.All(messages, message =>
        {
            Assert.Equal(("LifeOS", "You have a reminder"), (message.Title, message.Body));
            Assert.Equal(new Dictionary<string, string> { ["type"] = "reminder", ["id"] = reminder.Id.ToString("D") }, message.Data);
            // No personal text reaches the provider.
            Assert.DoesNotContain("Rossi", message.Title + message.Body + string.Join(",", message.Data.Values));
            Assert.DoesNotContain("results", message.Title + message.Body + string.Join(",", message.Data.Values));
        });

        Assert.Equal((DueLocal.AddDays(1), (ReminderOutcome?)ReminderOutcome.Notified), await NextAsync(reminder.Id));
    }

    [Fact]
    public async Task ConcurrentTicks_ClaimTheOccurrenceOnce()
    {
        var user = await NewUserAsync(Tokyo);
        await RegisterDeviceAsync(user.Id);
        var reminder = await AddAsync(user.Id);

        // Separate guards, scopes and connections: only the unique occurrence key decides.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => TickAsync(Due.AddMinutes(2))));

        Assert.Single(await ExecutionsAsync(user.Id));
        Assert.Single(await DeliveriesAsync(user.Id));
        Assert.Equal(DueLocal.AddDays(1), (await NextAsync(reminder.Id)).Next);
    }

    [Fact]
    public async Task LateTick_WithinTheLateness_FiresOnceForTheMissedTime()
    {
        var user = await NewUserAsync(Tokyo);
        var reminder = await AddAsync(user.Id);

        await TickAsync(Due.AddHours(3));
        await TickAsync(Due.AddHours(3).AddMinutes(10));

        var execution = Assert.Single(await ExecutionsAsync(user.Id));
        Assert.Equal((AutomationExecutionStatus.Succeeded, Due), (execution.Status, execution.ScheduledForUtc));
        Assert.Equal(DueLocal.AddDays(1), (await NextAsync(reminder.Id)).Next);
    }

    [Fact]
    public async Task LateTick_BeyondTheLateness_SkipsTheOccurrenceAsMissed_WithoutAPush()
    {
        var user = await NewUserAsync(Tokyo);
        var daily = await AddAsync(user.Id);
        var once = await AddAsync(user.Id, ReminderSchedule.Once(new DateOnly(2026, 11, 19), new TimeOnly(8, 30)));

        await TickAsync(Due.AddHours(7));

        Assert.Empty(await ExecutionsAsync(user.Id));
        Assert.Equal((DueLocal.AddDays(1), (ReminderOutcome?)ReminderOutcome.Missed), await NextAsync(daily.Id));
        Assert.Equal((ReminderStatus.Completed, (ReminderOutcome?)ReminderOutcome.Missed), await StatusAsync(once.Id));

        // The next day's occurrence is delivered normally.
        await TickAsync(Due.AddDays(1).AddMinutes(1));
        Assert.Equal([$"{daily.Id:D}:2026-11-20T08:30"], (await ExecutionsAsync(user.Id)).Select(row => row.OccurrenceKey));
    }

    [Fact]
    public async Task AFailedAttempt_IsRetried_AndNotifiesOnce()
    {
        var user = await NewUserAsync(Tokyo);
        await RegisterDeviceAsync(user.Id);
        var reminder = await AddAsync(user.Id);
        var failures = 1;

        await TickAsync(Due.AddMinutes(1), failFind: () => failures-- > 0);

        var failed = Assert.Single(await ExecutionsAsync(user.Id));
        Assert.Equal((AutomationExecutionStatus.FailedRetryable, AutomationExecutionPolicy.UnhandledCode, (DateTimeOffset?)Due.AddMinutes(11)),
            (failed.Status, failed.LastFailureCode, failed.NextAttemptAtUtc));
        Assert.Empty(await DeliveriesAsync(user.Id));
        Assert.Equal((DueLocal, (ReminderOutcome?)null), await NextAsync(reminder.Id));

        await TickAsync(Due.AddMinutes(12));    // Phase B retry
        await TickAsync(Due.AddMinutes(23));

        var retried = Assert.Single(await ExecutionsAsync(user.Id));
        Assert.Equal((AutomationExecutionStatus.Succeeded, 2), (retried.Status, retried.AttemptCount));
        Assert.Single(await DeliveriesAsync(user.Id));
        Assert.Equal(DueLocal.AddDays(1), (await NextAsync(reminder.Id)).Next);
    }

    [Fact]
    public async Task AFinallyFailedOccurrence_IsSkipped_NeverRediscovered()
    {
        var user = await NewUserAsync(Tokyo);
        var reminder = await AddAsync(user.Id);

        await TickAsync(Due.AddMinutes(1), failFind: () => true);
        await TickAsync(Due.AddMinutes(12), failFind: () => true);
        await TickAsync(Due.AddMinutes(43), failFind: () => true);

        // The third attempt fails for good (Phase B); the same tick's discovery (Phase C) then sees the
        // terminal key and moves the reminder on as Missed instead of claiming it again.
        var failed = Assert.Single(await ExecutionsAsync(user.Id));
        Assert.Equal((AutomationExecutionStatus.FailedFinal, AutomationExecutionPolicy.MaxAttempts, AutomationExecutionPolicy.MaxAttemptsReachedCode),
            (failed.Status, failed.AttemptCount, failed.LastFailureCode));
        Assert.Equal((DueLocal.AddDays(1), (ReminderOutcome?)ReminderOutcome.Missed), await NextAsync(reminder.Id));

        await TickAsync(Due.AddMinutes(53));
        Assert.Single(await ExecutionsAsync(user.Id));
    }

    [Fact]
    public async Task Tick_InsideQuietHours_DefersThePushUntilTheyEnd()
    {
        var user = await NewUserAsync(Tokyo);
        var device = await RegisterDeviceAsync(user.Id);
        // Daily 23:00 Tokyo = 14:00Z; default quiet hours 22:00–08:00 end at 08:00 Tokyo = 23:00Z.
        await AddAsync(user.Id, ReminderSchedule.Daily(new TimeOnly(23, 0)));
        var dueAt = new DateTimeOffset(2026, 11, 18, 14, 0, 0, TimeSpan.Zero);
        var sender = new FakePushNotificationSender();

        await TickAsync(dueAt.AddMinutes(1), sender);
        await TickAsync(dueAt.AddMinutes(11), sender);

        Assert.DoesNotContain(sender.Sent, sent => sent.Target.Token == device.PushToken);
        var deferred = Assert.Single(await DeliveriesAsync(user.Id));
        Assert.Equal((NotificationDeliveryStatus.Pending, 0, (DateTimeOffset?)new DateTimeOffset(2026, 11, 18, 23, 0, 0, TimeSpan.Zero)),
            (deferred.Status, deferred.AttemptCount, deferred.NextAttemptAtUtc));

        await TickAsync(new DateTimeOffset(2026, 11, 18, 23, 1, 0, TimeSpan.Zero), sender);

        Assert.Equal((NotificationDeliveryStatus.Sent, 1), ((await DeliveriesAsync(user.Id))[0].Status, (await DeliveriesAsync(user.Id))[0].AttemptCount));
        Assert.Single(sender.Sent, sent => sent.Target.Token == device.PushToken);
    }

    [Fact]
    public async Task AnFcmTransientFailure_IsRetriedPerDevice_FromTheOutbox()
    {
        var user = await NewUserAsync(Tokyo);
        var device = await RegisterDeviceAsync(user.Id);
        await AddAsync(user.Id);
        var transient = 1;
        var sender = new FakePushNotificationSender
        {
            Respond = (target, _) => Task.FromResult(target.Token == device.PushToken && transient-- > 0 ? PushSendResult.Transient : PushSendResult.Accepted)
        };

        await TickAsync(Due.AddMinutes(1), sender);
        await TickAsync(Due.AddMinutes(11), sender);    // attempt 1: transient

        var waiting = Assert.Single(await DeliveriesAsync(user.Id));
        Assert.Equal((NotificationDeliveryStatus.Pending, 1, (DateTimeOffset?)Due.AddMinutes(21)), (waiting.Status, waiting.AttemptCount, waiting.NextAttemptAtUtc));

        await TickAsync(Due.AddMinutes(22), sender);    // attempt 2: accepted

        Assert.Equal((NotificationDeliveryStatus.Sent, 2), ((await DeliveriesAsync(user.Id))[0].Status, (await DeliveriesAsync(user.Id))[0].AttemptCount));
        Assert.Single(await ExecutionsAsync(user.Id));
        Assert.Equal(2, sender.Sent.Count(sent => sent.Target.Token == device.PushToken));
    }

    [Fact]
    public async Task ASnooze_FiresAsItsOwnOccurrence_AndIsCleared()
    {
        var user = await NewUserAsync(Tokyo);
        await RegisterDeviceAsync(user.Id);
        var once = await AddAsync(user.Id, ReminderSchedule.Once(new DateOnly(2026, 11, 19), new TimeOnly(8, 30)));

        await TickAsync(Due.AddMinutes(1));
        Assert.Equal((ReminderStatus.Completed, (ReminderOutcome?)ReminderOutcome.Notified), await StatusAsync(once.Id));

        // Tapped at 08:40 local: snooze 15 min → 08:55 local = 23:55Z.
        await ChangeAsync(user.Id, once.Id, reminder => reminder.Snooze(15, Due.AddMinutes(10)));
        await TickAsync(Due.AddMinutes(26));

        Assert.Equal([$"{once.Id:D}:2026-11-19T08:30", $"{once.Id:D}:s:2026-11-18T23:55Z"],
            (await ExecutionsAsync(user.Id)).OrderBy(row => row.ScheduledForUtc).Select(row => row.OccurrenceKey));
        Assert.Equal(2, (await DeliveriesAsync(user.Id)).Count);
        Assert.Null((await SnoozeAsync(once.Id)));
        Assert.Equal(ReminderStatus.Completed, (await StatusAsync(once.Id)).Status);
    }

    [Fact]
    public async Task AMutedUser_GetsNoExecution_AndTheReminderWaits()
    {
        var user = await NewUserAsync(Tokyo);
        Assert.True(await SetPreferencesAsync(user.Id, userReminders: false));
        var reminder = await AddAsync(user.Id);

        await TickAsync(Due.AddMinutes(1));

        Assert.Empty(await ExecutionsAsync(user.Id));
        Assert.Equal(DueLocal, (await NextAsync(reminder.Id)).Next);
    }

    // ---- Helpers ----

    private async Task TickAsync(DateTimeOffset now, IPushNotificationSender? sender = null, Func<bool>? failFind = null)
    {
        await using var scope = fixture.CreateScope();
        var services = scope.ServiceProvider;
        var time = new FixedTimeProvider(now);
        var deliveries = services.GetRequiredService<INotificationDeliveryStore>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var dispatcher = new NotificationDispatcher(
            deliveries, services.GetRequiredService<IDeviceRegistrationRepository>(), Preferences(scope), unitOfWork, time, sender);
        IReminderScheduleStore store = failFind is null ? Store(scope) : new FailingFindStore(Store(scope), failFind);

        // The production wiring of the handler, from the scope's real store and preferences.
        IAutomationHandler[] handlers = [new ReminderAutomationHandler(store, Preferences(scope), time)];

        await new RunAutomationTick(handlers, services.GetRequiredService<IAutomationExecutionStore>(), dispatcher, deliveries, unitOfWork,
                new AutomationTickGuard(), time)
            .RunAsync();
    }

    private async Task<List<string>> DueKeysAsync(DateTimeOffset now, IReadOnlyCollection<Guid> users)
    {
        await using var scope = fixture.CreateScope();
        var handler = new ReminderAutomationHandler(Store(scope), Preferences(scope), new FixedTimeProvider(now));

        return (await handler.FindDueAsync(now, 1000, default)).Where(item => users.Contains(item.UserId)).Select(item => item.OccurrenceKey).ToList();
    }

    private async Task<Reminder> AddAsync(Guid userId, ReminderSchedule? schedule = null, DateTimeOffset? created = null, string zone = Tokyo,
        string title = "Take vitamins", string? note = null)
    {
        var at = created ?? Created;
        var local = TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(zone)).DateTime;
        var reminder = Reminder.Create(userId, title, note, schedule ?? ReminderSchedule.Daily(new TimeOnly(8, 30)), local, at);

        await using var scope = fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IReminderRepository>().AddAsync(reminder, default);
        return reminder;
    }

    private async Task ChangeAsync(Guid userId, Guid reminderId, Action<Reminder> change)
    {
        await using var scope = fixture.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IReminderRepository>();
        var reminder = (await repository.GetAsync(userId, reminderId, default))!;
        change(reminder);
        Assert.True(await repository.SaveAsync(reminder, default));
    }

    private async Task<(DateTime? Next, ReminderOutcome? Outcome)> NextAsync(Guid reminderId)
    {
        await using var scope = fixture.CreateScope();
        var row = await Db(scope).Reminders.AsNoTracking().SingleAsync(reminder => reminder.Id == reminderId);
        return (row.NextDueLocal, row.LastOutcome);
    }

    private async Task<(ReminderStatus Status, ReminderOutcome? Outcome)> StatusAsync(Guid reminderId)
    {
        await using var scope = fixture.CreateScope();
        var row = await Db(scope).Reminders.AsNoTracking().SingleAsync(reminder => reminder.Id == reminderId);
        return (row.Status, row.LastOutcome);
    }

    private async Task<DateTimeOffset?> SnoozeAsync(Guid reminderId)
    {
        await using var scope = fixture.CreateScope();
        return (await Db(scope).Reminders.AsNoTracking().SingleAsync(reminder => reminder.Id == reminderId)).SnoozeUntilUtc;
    }

    private async Task<List<AutomationExecution>> ExecutionsAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).AutomationExecutions.AsNoTracking().Where(row => row.UserId == userId).ToListAsync();
    }

    private async Task<List<NotificationDelivery>> DeliveriesAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).NotificationDeliveries.AsNoTracking().Where(row => row.UserId == userId).OrderBy(row => row.Id).ToListAsync();
    }

    private async Task<DeviceRegistration> RegisterDeviceAsync(Guid userId)
    {
        var registration = DeviceRegistration.Register(userId, $"install-{Guid.NewGuid():N}", DevicePlatform.Android, $"token-{Guid.NewGuid():N}", true,
            Created.AddDays(-1));
        await using var scope = fixture.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IDeviceRegistrationRepository>().UpsertAsync(registration, default));
        return await Db(scope).DeviceRegistrations.AsNoTracking().SingleAsync(row => row.UserId == userId && row.PushToken == registration.PushToken);
    }

    private async Task<bool> SetPreferencesAsync(Guid userId, bool userReminders)
    {
        await using var scope = fixture.CreateScope();
        return await Preferences(scope).SetAsync(
            NotificationPreferences.Create(userId, true, true, userReminders, QuietHours.Default, Created), default);
    }

    private async Task<User> NewUserAsync(string? timeZoneId = null)
    {
        var user = User.CreateFromExternalIdentity(null, null, Created.AddDays(-400));

        if (timeZoneId is not null)
        {
            user.SetTimeZone(timeZoneId);
        }

        await PostgresAssert.InsertAsync(fixture, user);
        return user;
    }

    private async Task Execute(string sql)
    {
        await using var scope = fixture.CreateScope();
        await Db(scope).Database.ExecuteSqlRawAsync(sql);
    }

    private static IReminderScheduleStore Store(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IReminderScheduleStore>();

    private static INotificationPreferencesRepository Preferences(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<INotificationPreferencesRepository>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static Task<List<string>> Strings(DatabaseFacade database, string sql) => database.SqlQueryRaw<string>(sql).ToListAsync();

    // A transient database failure while the attempt re-reads the reminder (an exception → "Unhandled").
    private sealed class FailingFindStore(IReminderScheduleStore inner, Func<bool> fail) : IReminderScheduleStore
    {
        public Task<IReadOnlyList<string>> GetUserTimeZoneIdsAsync(CancellationToken cancellationToken) => inner.GetUserTimeZoneIdsAsync(cancellationToken);

        public Task<IReadOnlyList<ReminderCandidate>> FindCandidatesAsync(IReadOnlyList<ZoneLocalNow> zones, DateTimeOffset nowUtc, int limit,
            CancellationToken cancellationToken) => inner.FindCandidatesAsync(zones, nowUtc, limit, cancellationToken);

        public Task<IReadOnlyDictionary<string, bool>> GetExecutionsAsync(string automationType, IReadOnlyList<(Guid UserId, string OccurrenceKey)> occurrences,
            CancellationToken cancellationToken) => inner.GetExecutionsAsync(automationType, occurrences, cancellationToken);

        public Task SkipMissedAsync(IReadOnlyList<MissedOccurrence> occurrences, IReadOnlyList<MissedSnooze> snoozes, DateTimeOffset nowUtc,
            CancellationToken cancellationToken) => inner.SkipMissedAsync(occurrences, snoozes, nowUtc, cancellationToken);

        public Task<Reminder?> FindAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken) =>
            fail() ? throw new TimeoutException("Simulated database timeout.") : inner.FindAsync(userId, reminderId, cancellationToken);

        public Task AdvanceAsync(Guid userId, ReminderOccurrence occurrence, ReminderOutcome outcome, DateTimeOffset nowUtc,
            CancellationToken cancellationToken) => inner.AdvanceAsync(userId, occurrence, outcome, nowUtc, cancellationToken);
    }
}
