using LifeOS.Application.Automation;
using LifeOS.Application.Habits;
using LifeOS.Application.Notifications;
using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Application.Persistence;
using LifeOS.Application.Reminders;
using LifeOS.Domain.Automation;
using LifeOS.Domain.Habits;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Nutrition.Hydration;
using LifeOS.Domain.Reminders;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// REM-001 R5 against real PostgreSQL: the AddConditionalReminders schema (habit FK cascade, one nudge per
// habit / one water nudge per user, the new switches, NotRelevant), the per-kind discovery SQL, the claim
// that returns the source occurrence key, and the conditional handler end to end through the real tick,
// outbox and dispatcher with the Reminders relevance check over the real Habits and Hydration reads.
//
// The database is shared by the whole PostgreSQL collection, so every assertion is scoped to this test's
// own users. Asia/Tokyo (UTC+9, no DST); the dates (Dec 2026) are not used by other reminder tests.
[Collection(PostgreSqlCollection.Name)]
public class ConditionalReminderTickTests(PostgreSqlFixture fixture)
{
    private const string Tokyo = "Asia/Tokyo";
    private const int TokyoOffset = 540;

    // Created Tue 1 Dec 2026 20:00 Tokyo (11:00Z); a Daily 20:00 is due Wed 2 Dec 20:00 Tokyo = 11:00Z.
    private static readonly DateTimeOffset Created = new(2026, 12, 1, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due = new(2026, 12, 2, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly DueDate = new(2026, 12, 2);
    private static readonly DateTime DueLocal = new(2026, 12, 2, 20, 0, 0);

    // ---- Schema ----

    [Fact]
    public async Task Migration_AddsTheSwitches_TrueByDefault()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddConditionalReminders", StringComparison.Ordinal));
        Assert.Equal(
            ["habit_reminders_enabled boolean NOT NULL DEFAULT true", "hydration_reminders_enabled boolean NOT NULL DEFAULT true"],
            await Strings(database,
                """
                SELECT column_name || ' ' || data_type || CASE WHEN is_nullable = 'NO' THEN ' NOT NULL' ELSE '' END || ' DEFAULT ' || column_default AS "Value"
                FROM information_schema.columns
                WHERE table_name = 'notification_preferences' AND column_name IN ('habit_reminders_enabled', 'hydration_reminders_enabled')
                ORDER BY column_name
                """));

        // Saved and read back by the repository.
        var user = await NewUserAsync();
        Assert.True(await Preferences(scope).SetAsync(NotificationPreferences.Create(user.Id, true, true, true, QuietHours.Default, Created,
            habitRemindersEnabled: false, hydrationRemindersEnabled: true), default));
        var saved = await Preferences(scope).GetAsync(user.Id, default);
        Assert.Equal((false, true), (saved.HabitRemindersEnabled, saved.HydrationRemindersEnabled));
    }

    [Fact]
    public async Task HabitReminders_FollowTheirOwnersHabit_OnePerHabit_OneWaterReminderPerUser()
    {
        var owner = await NewUserAsync();
        var other = await NewUserAsync();
        var habit = await AddHabitAsync(owner.Id, "Read 20 pages");

        // The composite key: another user's reminder can never reference this habit.
        await PostgresAssert.ViolatesAsync(PostgresAssert.ForeignKeyViolation, "FK_reminders_habits",
            () => AddAsync(other.Id, ReminderKind.HabitIncomplete, habit.Id));

        var nudge = await AddAsync(owner.Id, ReminderKind.HabitIncomplete, habit.Id);
        await AddAsync(owner.Id, ReminderKind.HydrationGoal);

        var forHabit = await Assert.ThrowsAsync<ReminderConflictException>(() => AddAsync(owner.Id, ReminderKind.HabitIncomplete, habit.Id));
        Assert.Equal(ReminderConflictException.ExistsForHabit, forHabit.Code);
        var water = await Assert.ThrowsAsync<ReminderConflictException>(() => AddAsync(owner.Id, ReminderKind.HydrationGoal));
        Assert.Equal(ReminderConflictException.HydrationExists, water.Code);
        await AddAsync(other.Id, ReminderKind.HydrationGoal);

        await using (var scope = fixture.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IReminderRepository>();
            Assert.Equal("Read 20 pages", (await repository.GetHabitNamesAsync(owner.Id, [habit.Id], default))[habit.Id]);
            Assert.Empty(await repository.GetHabitNamesAsync(other.Id, [habit.Id], default));

            var stored = (await repository.GetAsync(owner.Id, nudge.Id, default))!;
            Assert.Equal((ReminderKind.HabitIncomplete, (Guid?)habit.Id, (string?)null), (stored.Kind, stored.HabitId, stored.Title));

            // Deleting the habit deletes its nudge (and nothing else).
            Assert.True(await scope.ServiceProvider.GetRequiredService<IHabitRepository>().DeleteAsync(owner.Id, habit.Id, default));
            Assert.Null(await repository.GetAsync(owner.Id, nudge.Id, default));
            Assert.Single(await repository.GetAllAsync(owner.Id, Created, default));
        }
    }

    // ---- Discovery SQL ----

    [Fact]
    public async Task Discovery_ReturnsOnlyTheRequestedKinds_AndSkipsTheMutedType()
    {
        var user = await NewUserAsync();
        var habit = await AddHabitAsync(user.Id, "Stretch");
        var custom = Reminder.Create(user.Id, "Take vitamins", null, ReminderSchedule.Daily(new TimeOnly(20, 0)), Local(Created), Created);
        await AddAsync(custom);
        var nudge = await AddAsync(user.Id, ReminderKind.HabitIncomplete, habit.Id);
        var water = await AddAsync(user.Id, ReminderKind.HydrationGoal);

        Assert.Equal(new[] { nudge.Id, water.Id }.Order(), await CandidatesAsync(user.Id, [ReminderKind.HabitIncomplete, ReminderKind.HydrationGoal]));
        Assert.Equal([custom.Id], await CandidatesAsync(user.Id, [ReminderKind.Custom]));

        await SetPreferencesAsync(user.Id, habit: false, hydration: true, personal: true);
        Assert.Equal([water.Id], await CandidatesAsync(user.Id, [ReminderKind.HabitIncomplete, ReminderKind.HydrationGoal]));

        await SetPreferencesAsync(user.Id, habit: true, hydration: false, personal: false);
        Assert.Equal([nudge.Id], await CandidatesAsync(user.Id, [ReminderKind.HabitIncomplete, ReminderKind.HydrationGoal]));
        Assert.Empty(await CandidatesAsync(user.Id, [ReminderKind.Custom]));
    }

    // ---- Claim (§6.6) ----

    [Fact]
    public async Task TheClaim_CarriesTheSourceOccurrenceKey_OnlyForAutomationDeliveries()
    {
        var user = await NewUserAsync();
        await RegisterDeviceAsync(user.Id);
        await SetWaterTargetAsync(user.Id, 2000);
        var reminder = await AddAsync(user.Id, ReminderKind.HydrationGoal);

        // Without a push sender nothing is dispatched: the tick only executes and enqueues.
        await TickAsync(Due, sender: null);
        var execution = await ExecutionAsync(user.Id);
        Assert.Equal(ConditionalReminderAutomationHandler.Type, execution.AutomationType);

        await using var scope = fixture.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<INotificationDeliveryStore>();
        var automation = (await store.ClaimNextForNotificationAsync($"automation:{execution.Id:D}", Due.AddMinutes(1), TimeSpan.FromMinutes(5), 5, default))!;
        Assert.Equal((NotificationType.HydrationReminder, "hydration", (Guid?)null), (automation.Type, automation.ResourceType, automation.ResourceId));
        Assert.Equal(ReminderOccurrenceKey.Format(ReminderOccurrence.Scheduled(reminder.Id, DueLocal)), automation.SourceOccurrenceKey);

        var test = LogicalNotification.Test(user.Id, Due);
        await store.EnqueueAsync(test, Due, default);
        var claimedTest = (await store.ClaimNextForNotificationAsync(test.NotificationKey, Due.AddMinutes(1), TimeSpan.FromMinutes(5), 5, default))!;
        Assert.Null(claimedTest.SourceOccurrenceKey);

        // The widened check constraint accepts the new code.
        Assert.True(await store.CompleteFailedAsync(automation.DeliveryId, automation.Attempt, NotificationErrorCode.NotRelevant, Due.AddMinutes(1), default));
    }

    // ---- End to end ----

    [Fact]
    public async Task ADoneHabit_IsSuppressed_WithoutAnyDelivery()
    {
        var user = await NewUserAsync();
        await RegisterDeviceAsync(user.Id);
        var habit = await AddHabitAsync(user.Id, "Read");
        var reminder = await AddAsync(user.Id, ReminderKind.HabitIncomplete, habit.Id);

        await using (var scope = fixture.CreateScope())
        {
            var habits = scope.ServiceProvider.GetRequiredService<IHabitRepository>();
            var stored = (await habits.GetAsync(user.Id, habit.Id, default))!;
            Assert.True(await habits.UpsertCheckInAsync(stored.CheckIn(DueDate, HabitCheckInStatus.Done, null, DueDate, Due), default));
        }

        await TickAsync(Due);

        Assert.Equal(AutomationExecutionStatus.Succeeded, (await ExecutionAsync(user.Id)).Status);
        Assert.Empty(await DeliveriesAsync(user.Id));
        var row = await ReminderAsync(reminder.Id);
        Assert.Equal((ReminderOutcome.Suppressed, (DateTime?)DueLocal.AddDays(1)), (row.LastOutcome, row.NextDueLocal));
    }

    [Fact]
    public async Task AWaterNudge_IsSent_OnlyWhileTheTargetIsOpen_AtEachAttempt()
    {
        var reached = await NewUserAsync();
        var open = await NewUserAsync();

        foreach (var user in new[] { reached, open })
        {
            await RegisterDeviceAsync(user.Id);
            await SetWaterTargetAsync(user.Id, 2000);
            await DrinkAsync(user.Id, 1000);
            await AddAsync(user.Id, ReminderKind.HydrationGoal);
        }

        var sender = new FakePushNotificationSender();
        await TickAsync(Due, sender);

        Assert.Single(await DeliveriesAsync(reached.Id));
        Assert.Single(await DeliveriesAsync(open.Id));
        Assert.Equal(ReminderOutcome.Notified, (await ReminderOfAsync(reached.Id)).LastOutcome);

        // Between enqueue and send one user reaches the target: only the other is sent.
        await DrinkAsync(reached.Id, 1000);
        await TickAsync(Due.AddMinutes(10), sender);

        var notRelevant = Assert.Single(await DeliveriesAsync(reached.Id));
        Assert.Equal((NotificationDeliveryStatus.Failed, (NotificationErrorCode?)NotificationErrorCode.NotRelevant), (notRelevant.Status, notRelevant.LastErrorCode));
        Assert.Equal(NotificationDeliveryStatus.Sent, Assert.Single(await DeliveriesAsync(open.Id)).Status);
        Assert.Equal(ReminderOutcome.Notified, (await ReminderOfAsync(reached.Id)).LastOutcome);

        var tokens = sender.Sent.Select(sent => sent.Target.Token).ToList();
        Assert.Contains(await TokenAsync(open.Id), tokens);
        Assert.DoesNotContain(await TokenAsync(reached.Id), tokens);
        Assert.All(sender.Sent, sent => Assert.Equal("Your water target for today isn't reached yet", sent.Message.Body));
    }

    // ---- Helpers ----

    private async Task<AutomationExecution> ExecutionAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).AutomationExecutions.AsNoTracking().SingleAsync(execution => execution.UserId == userId);
    }

    private async Task TickAsync(DateTimeOffset now, IPushNotificationSender? sender = null)
    {
        await using var scope = fixture.CreateScope();
        var services = scope.ServiceProvider;
        var time = new FixedTimeProvider(now);
        var deliveries = services.GetRequiredService<INotificationDeliveryStore>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var store = services.GetRequiredService<IReminderScheduleStore>();
        var water = new HydrationDayReader(services.GetRequiredService<IHydrationRepository>());
        var evaluator = new ReminderConditionEvaluator(new HabitDayStatusReader(services.GetRequiredService<IHabitRepository>(), water), water);

        // The production wiring: the conditional handler and, in the dispatcher, the same evaluator's relevance check.
        var dispatcher = new NotificationDispatcher(deliveries, services.GetRequiredService<IDeviceRegistrationRepository>(), Preferences(scope),
            unitOfWork, time, sender, [new ReminderRelevanceCheck(store, Preferences(scope), evaluator)]);
        IAutomationHandler[] handlers = [new ConditionalReminderAutomationHandler(store, Preferences(scope), evaluator, time)];

        await new RunAutomationTick(handlers, services.GetRequiredService<IAutomationExecutionStore>(), dispatcher, deliveries, unitOfWork,
                new AutomationTickGuard(), time)
            .RunAsync();
    }

    private async Task<List<Guid>> CandidatesAsync(Guid userId, IReadOnlyList<ReminderKind> kinds)
    {
        await using var scope = fixture.CreateScope();
        var candidates = await scope.ServiceProvider.GetRequiredService<IReminderScheduleStore>()
            .FindCandidatesAsync(kinds, [new ZoneLocalNow(Tokyo, DueLocal)], Due, 1000, default);

        return candidates.Where(candidate => candidate.UserId == userId).Select(candidate => candidate.ReminderId).Order().ToList();
    }

    private static DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(Tokyo)).DateTime;

    private Task<Reminder> AddAsync(Guid userId, ReminderKind kind, Guid? habitId = null) =>
        AddAsync(Reminder.CreateConditional(userId, kind, habitId, ReminderSchedule.Daily(new TimeOnly(20, 0)), Local(Created), Created));

    private async Task<Reminder> AddAsync(Reminder reminder)
    {
        await using var scope = fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IReminderRepository>().AddAsync(reminder, default);
        return reminder;
    }

    private async Task<Habit> AddHabitAsync(Guid userId, string name)
    {
        var habit = Habit.Create(userId, name, null, HabitCompletionSource.Manual, HabitFrequency.Daily, DueDate.AddDays(-7), DueDate, Created);
        await using var scope = fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IHabitRepository>().AddAsync(habit, default);
        return habit;
    }

    private async Task SetWaterTargetAsync(Guid userId, int targetMl)
    {
        await using var scope = fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IHydrationRepository>()
            .UpsertTargetVersionAsync(HydrationTargetVersion.Create(userId, DueDate.AddDays(-1), targetMl, Created), default);
    }

    private async Task DrinkAsync(Guid userId, int ml)
    {
        await using var scope = fixture.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IHydrationRepository>()
            .AddAsync(HydrationIntake.Create(userId, ml, DueDate, new TimeOnly(12, 0), TokyoOffset, Due), default);
    }

    private async Task SetPreferencesAsync(Guid userId, bool habit, bool hydration, bool personal)
    {
        await using var scope = fixture.CreateScope();
        Assert.True(await Preferences(scope).SetAsync(NotificationPreferences.Create(userId, true, true, personal, QuietHours.Default, Created,
            habit, hydration), default));
    }

    private async Task RegisterDeviceAsync(Guid userId)
    {
        var registration = DeviceRegistration.Register(userId, $"install-{Guid.NewGuid():N}", DevicePlatform.Android, $"token-{Guid.NewGuid():N}", true,
            Created.AddDays(-1));
        await using var scope = fixture.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IDeviceRegistrationRepository>().UpsertAsync(registration, default));
    }

    private async Task<string> TokenAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return (await Db(scope).DeviceRegistrations.AsNoTracking().SingleAsync(row => row.UserId == userId)).PushToken!;
    }

    private async Task<List<NotificationDelivery>> DeliveriesAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).NotificationDeliveries.AsNoTracking().Where(row => row.UserId == userId).ToListAsync();
    }

    private async Task<Reminder> ReminderAsync(Guid reminderId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).Reminders.AsNoTracking().SingleAsync(reminder => reminder.Id == reminderId);
    }

    private async Task<Reminder> ReminderOfAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).Reminders.AsNoTracking().SingleAsync(reminder => reminder.UserId == userId);
    }

    private async Task<User> NewUserAsync()
    {
        var user = User.CreateFromExternalIdentity(null, null, Created.AddDays(-400));
        user.SetTimeZone(Tokyo);
        await PostgresAssert.InsertAsync(fixture, user);
        return user;
    }

    private static INotificationPreferencesRepository Preferences(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<INotificationPreferencesRepository>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static async Task<List<string>> Strings(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, string sql) =>
        await database.SqlQueryRaw<string>(sql).ToListAsync();
}
