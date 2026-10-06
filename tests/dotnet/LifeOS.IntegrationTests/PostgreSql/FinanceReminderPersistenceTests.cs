using LifeOS.Application.Automation;
using LifeOS.Application.Finance.PlannedExpenses;
using LifeOS.Application.Finance.Recurring;
using LifeOS.Application.Finance.Reminders;
using LifeOS.Application.Notifications;
using LifeOS.Application.Persistence;
using LifeOS.Domain.Automation;
using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.PlannedExpenses;
using LifeOS.Domain.Finance.Recurring;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Users;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.PostgreSql;

// AUTO-003A against real PostgreSQL: the notification_preferences schema and repository, the delivery
// deferral statement, the Finance reminder discovery SQL (FIN-004/FIN-005 due rules, preferences,
// occurrence keys), and both reminder handlers end to end through the real tick engine, stores, unit of
// work and outbox, including quiet hours.
//
// The database is shared by the whole PostgreSQL collection, so every assertion is scoped to this
// test's own users and rows. Asia/Kolkata (UTC+05:30, no DST) is not used by any other test.
[Collection(PostgreSqlCollection.Name)]
public class FinanceReminderPersistenceTests(PostgreSqlFixture fixture)
{
    private const string Zone = "Asia/Kolkata";
    private static readonly DateOnly Date = new(2026, 11, 15);

    // 2026-11-15 09:00 IST = 03:30Z.
    private static readonly DateTimeOffset Due = new(2026, 11, 15, 3, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Due.AddMinutes(5);

    // ---- Schema ----

    [Fact]
    public async Task Migration_CreatesTheNotificationPreferencesTable()
    {
        await using var scope = fixture.CreateScope();
        var database = Db(scope).Database;

        Assert.Contains(await database.GetAppliedMigrationsAsync(), id => id.EndsWith("_AddNotificationPreferences", StringComparison.Ordinal));
        Assert.False(database.HasPendingModelChanges());

        Assert.Equal(
            [
                "user_id uuid NOT NULL", "recurring_transaction_reminders_enabled boolean NOT NULL",
                "planned_expense_reminders_enabled boolean NOT NULL", "quiet_hours_start time without time zone NOT NULL",
                "quiet_hours_end time without time zone NOT NULL", "updated_at_utc timestamp with time zone NOT NULL"
            ],
            await Strings(database,
                """
                SELECT attname || ' ' || format_type(atttypid, atttypmod) || CASE WHEN attnotnull THEN ' NOT NULL' ELSE ' NULL' END AS "Value"
                FROM pg_attribute WHERE attrelid = 'notification_preferences'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum
                """));
        Assert.Equal(
            ["CREATE UNIQUE INDEX \"PK_notification_preferences\" ON public.notification_preferences USING btree (user_id)"],
            await Strings(database, "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'notification_preferences'"));
        Assert.Equal(
            ["FK_notification_preferences_users_user_id c", "ck_notification_preferences_quiet_hours"],
            (await Strings(database,
                """
                SELECT conname::text || CASE WHEN contype = 'f' THEN ' ' || confdeltype::text ELSE '' END AS "Value" FROM pg_constraint
                WHERE conrelid = 'notification_preferences'::regclass AND contype IN ('c', 'f')
                """)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task InvalidQuietHours_AreRejectedByTheDatabase()
    {
        var user = await NewUserAsync();
        Assert.True(await SetAsync(user.Id, true, true, new TimeOnly(22, 0), new TimeOnly(8, 0)));

        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_notification_preferences_quiet_hours", () =>
            Execute($"UPDATE notification_preferences SET quiet_hours_end = '22:00' WHERE user_id = '{user.Id}'"));
        await PostgresAssert.ViolatesAsync(PostgresAssert.CheckViolation, "ck_notification_preferences_quiet_hours", () =>
            Execute($"UPDATE notification_preferences SET quiet_hours_start = '21:59:30' WHERE user_id = '{user.Id}'"));
    }

    // ---- Preferences repository ----

    [Fact]
    public async Task Preferences_Default_RoundTrip_AndUserIsolation()
    {
        var user = await NewUserAsync(Zone);
        var other = await NewUserAsync();

        await using (var scope = fixture.CreateScope())
        {
            var defaults = await Preferences(scope).GetAsync(user.Id, default);
            Assert.Equal((true, true, QuietHours.Default), (defaults.RecurringTransactionRemindersEnabled, defaults.PlannedExpenseRemindersEnabled, defaults.QuietHours));
        }

        Assert.True(await SetAsync(user.Id, false, true, new TimeOnly(23, 30), new TimeOnly(6, 45)));
        Assert.True(await SetAsync(user.Id, false, false, new TimeOnly(23, 30), new TimeOnly(6, 45)));

        await using (var scope = fixture.CreateScope())
        {
            var saved = await Preferences(scope).GetAsync(user.Id, default);
            Assert.Equal((false, false, new TimeOnly(23, 30), new TimeOnly(6, 45), Now),
                (saved.RecurringTransactionRemindersEnabled, saved.PlannedExpenseRemindersEnabled, saved.QuietHoursStart, saved.QuietHoursEnd, saved.UpdatedAtUtc));

            var untouched = await Preferences(scope).GetAsync(other.Id, default);
            Assert.Equal((true, true, QuietHours.Default), (untouched.RecurringTransactionRemindersEnabled, untouched.PlannedExpenseRemindersEnabled, untouched.QuietHours));

            Assert.Equal(new QuietHoursContext(Zone, QuietHours.Create(new TimeOnly(23, 30), new TimeOnly(6, 45))),
                await Preferences(scope).GetQuietHoursContextAsync(user.Id, default));
            Assert.Equal(new QuietHoursContext(null, QuietHours.Default), await Preferences(scope).GetQuietHoursContextAsync(other.Id, default));
            Assert.Null(await Preferences(scope).GetQuietHoursContextAsync(Guid.CreateVersion7(), default));
        }
    }

    [Fact]
    public async Task Preferences_ForAMissingUser_AreNotSaved_AndDeletingTheUserDeletesThem()
    {
        Assert.False(await SetAsync(Guid.CreateVersion7(), true, true, new TimeOnly(22, 0), new TimeOnly(8, 0)));

        var user = await NewUserAsync();
        Assert.True(await SetAsync(user.Id, false, false, new TimeOnly(22, 0), new TimeOnly(8, 0)));

        await Execute($"DELETE FROM users WHERE id = '{user.Id}'");

        await using var scope = fixture.CreateScope();
        Assert.False(await Db(scope).NotificationPreferences.AnyAsync(row => row.UserId == user.Id));
    }

    // ---- Delivery deferral ----

    [Fact]
    public async Task Deferral_ReturnsToPending_GivesTheAttemptBack_AndIsFenced()
    {
        var user = await NewUserAsync(Zone);
        await RegisterDeviceAsync(user.Id);

        await using var scope = fixture.CreateScope();
        var deliveries = scope.ServiceProvider.GetRequiredService<INotificationDeliveryStore>();
        var notification = LogicalNotification.Test(user.Id, Now);
        await deliveries.EnqueueAsync(notification, Now, default);
        var claimed = (await deliveries.ClaimNextForNotificationAsync(notification.NotificationKey, Now, TimeSpan.FromMinutes(5), 5, default))!;

        Assert.False(await deliveries.CompleteDeferredAsync(claimed.DeliveryId, claimed.Attempt + 1, Now.AddHours(1), default));
        Assert.True(await deliveries.CompleteDeferredAsync(claimed.DeliveryId, claimed.Attempt, Now.AddHours(1), default));

        var row = await Db(scope).NotificationDeliveries.AsNoTracking().SingleAsync(delivery => delivery.Id == claimed.DeliveryId);
        Assert.Equal((NotificationDeliveryStatus.Pending, 0, (DateTimeOffset?)Now.AddHours(1), (DateTimeOffset?)null),
            (row.Status, row.AttemptCount, row.NextAttemptAtUtc, row.LeaseExpiresAtUtc));
    }

    // ---- Recurring discovery ----

    [Fact]
    public async Task RecurringDiscovery_FindsOnlyUnprocessedInRangeOccurrencesScheduledOnTheOpenDate()
    {
        var user = await NewUserAsync(Zone);
        var (account, category) = await FinanceAsync(user.Id);
        var due = await RuleAsync(user.Id, account, category, day: 15);
        var income = await RuleAsync(user.Id, account, category, day: 15, TransactionType.Income);
        var otherDay = await RuleAsync(user.Id, account, category, day: 16);
        var notStarted = await RuleAsync(user.Id, account, category, day: 15, startMonth: 12);
        var ended = await RuleAsync(user.Id, account, category, day: 15, endMonth: 10);
        var skipped = await RuleAsync(user.Id, account, category, day: 15);
        var confirmed = await RuleAsync(user.Id, account, category, day: 15);
        var transaction = Transaction.CreateExpense(user.Id, account.Id, category.Id, 900m, "EUR", Due, null, Due);
        await PostgresAssert.InsertAsync(fixture, transaction);
        await PostgresAssert.InsertAsync(fixture,
            RecurringOccurrenceState.Create(skipped, 2026, 11, OccurrenceStatus.Skipped, null, Due),
            RecurringOccurrenceState.Create(confirmed, 2026, 11, OccurrenceStatus.Confirmed, transaction.Id, Due),
            RecurringOccurrenceState.Create(due, 2026, 10, OccurrenceStatus.Skipped, null, Due));

        var found = await DueRuleIdsAsync(user.Id, Date);

        Assert.Equal(new[] { due.Id, income.Id }.Order(), found.Order());
        Assert.DoesNotContain(otherDay.Id, found);
        Assert.DoesNotContain(notStarted.Id, found);
        Assert.DoesNotContain(ended.Id, found);
    }

    [Fact]
    public async Task RecurringDiscovery_ClampsLateDaysToTheLastDayOfTheMonth()
    {
        var user = await NewUserAsync(Zone);
        var (account, category) = await FinanceAsync(user.Id);
        var day31 = await RuleAsync(user.Id, account, category, day: 31);
        var day30 = await RuleAsync(user.Id, account, category, day: 30);
        var day29 = await RuleAsync(user.Id, account, category, day: 29);

        // November has 30 days: 30 and 31 are due on the 30th; 31 is never due on the 29th.
        Assert.Equal(new[] { day31.Id, day30.Id }.Order(), (await DueRuleIdsAsync(user.Id, new DateOnly(2026, 11, 30))).Order());
        Assert.Equal([day29.Id], await DueRuleIdsAsync(user.Id, new DateOnly(2026, 11, 29)));
    }

    [Fact]
    public async Task RecurringDiscovery_RespectsThePreference_AndOtherUsersZones()
    {
        var disabled = await NewUserAsync(Zone);
        var elsewhere = await NewUserAsync("Asia/Dubai");
        var (accountA, categoryA) = await FinanceAsync(disabled.Id);
        var (accountB, categoryB) = await FinanceAsync(elsewhere.Id);
        await RuleAsync(disabled.Id, accountA, categoryA, day: 15);
        await RuleAsync(elsewhere.Id, accountB, categoryB, day: 15);
        Assert.True(await SetAsync(disabled.Id, recurring: false, planned: true, QuietHours.DefaultStart, QuietHours.DefaultEnd));

        Assert.Empty(await DueRuleIdsAsync(disabled.Id, Date));

        await using var scope = fixture.CreateScope();
        var found = await Reminders(scope).FindDueRecurringAsync(RecurringTransactionReminderHandler.Type, [new FinanceReminderOpenZone(Zone, Date)], 1000, default);
        Assert.DoesNotContain(found, item => item.UserId == elsewhere.Id);
    }

    // ---- Planned expense discovery ----

    [Fact]
    public async Task PlannedDiscovery_FindsOnlyUnresolvedExpensesScheduledOnTheOpenDate()
    {
        var user = await NewUserAsync(Zone);
        var (account, category) = await FinanceAsync(user.Id);
        var due = await PlannedAsync(user.Id, account, category, Date);
        var tomorrow = await PlannedAsync(user.Id, account, category, Date.AddDays(1));
        var yesterday = await PlannedAsync(user.Id, account, category, Date.AddDays(-1));
        var cancelled = await PlannedAsync(user.Id, account, category, Date);
        var confirmed = await PlannedAsync(user.Id, account, category, Date);
        var transaction = Transaction.CreateExpense(user.Id, account.Id, category.Id, 250m, "EUR", Due, null, Due);
        await PostgresAssert.InsertAsync(fixture, transaction);
        await PostgresAssert.InsertAsync(fixture,
            PlannedExpenseState.Cancel(cancelled, Date, Due),
            PlannedExpenseState.Confirm(confirmed, Date, transaction, Due));

        Assert.Equal([due.Id], await DuePlannedIdsAsync(user.Id, Date));
        Assert.DoesNotContain(tomorrow.Id, await DuePlannedIdsAsync(user.Id, Date));
        Assert.DoesNotContain(yesterday.Id, await DuePlannedIdsAsync(user.Id, Date));

        Assert.True(await SetAsync(user.Id, recurring: true, planned: false, QuietHours.DefaultStart, QuietHours.DefaultEnd));
        Assert.Empty(await DuePlannedIdsAsync(user.Id, Date));
    }

    // ---- End to end ----

    [Fact]
    public async Task Tick_RemindsEachDueItemOnce_ThroughTheOutbox_WithoutTouchingFinance()
    {
        var user = await NewUserAsync(Zone);
        var device = await RegisterDeviceAsync(user.Id);
        var (account, category) = await FinanceAsync(user.Id);
        var rule = await RuleAsync(user.Id, account, category, day: 15);
        var expense = await PlannedAsync(user.Id, account, category, Date);
        var before = await FinanceFingerprintAsync(user.Id);

        await TickAsync(Now);
        await TickAsync(Now.AddMinutes(10));

        await using var scope = fixture.CreateScope();
        var executions = await Db(scope).AutomationExecutions.AsNoTracking().Where(row => row.UserId == user.Id).OrderBy(row => row.AutomationType).ToListAsync();
        Assert.Equal(
            [
                (PlannedExpenseReminderHandler.Type, $"{expense.Id:D}:2026-11-15", AutomationExecutionStatus.Succeeded, Due, Due.AddHours(24), Zone),
                (RecurringTransactionReminderHandler.Type, $"{rule.Id:D}:2026-11", AutomationExecutionStatus.Succeeded, Due, Due.AddHours(24), Zone)
            ],
            executions.Select(row => (row.AutomationType, row.OccurrenceKey, row.Status, row.ScheduledForUtc, row.ExpiresAtUtc, row.TimeZoneId)));

        var deliveries = await Db(scope).NotificationDeliveries.AsNoTracking().Where(row => row.UserId == user.Id).OrderBy(row => row.NotificationType).ToListAsync();
        Assert.Equal(
            [
                (NotificationType.RecurringTransactionReminder, "finance_recurring", (Guid?)rule.Id, device.Id),
                (NotificationType.PlannedExpenseReminder, "finance_planned_expense", (Guid?)expense.Id, device.Id)
            ],
            deliveries.OrderBy(row => row.NotificationType == NotificationType.PlannedExpenseReminder)
                .Select(row => (row.NotificationType, row.ResourceType!, row.ResourceId, row.DeviceRegistrationId)));
        Assert.All(deliveries, row => Assert.StartsWith(LogicalNotification.AutomationKeyPrefix, row.NotificationKey));

        Assert.Equal(before, await FinanceFingerprintAsync(user.Id));
    }

    [Fact]
    public async Task Tick_InsideTheUsersQuietHours_DefersTheReminderUntilTheyEnd()
    {
        var user = await NewUserAsync(Zone);
        var device = await RegisterDeviceAsync(user.Id);
        var (account, category) = await FinanceAsync(user.Id);
        await PlannedAsync(user.Id, account, category, Date);
        Assert.True(await SetAsync(user.Id, true, true, new TimeOnly(8, 0), new TimeOnly(10, 0)));
        var sender = new FakePushNotificationSender();

        await TickAsync(Now, sender);                       // 09:05 IST: execution + Pending delivery
        await TickAsync(Now.AddMinutes(10), sender);        // 09:15 IST: Phase A defers it

        // Phase A also serves other tests' due deliveries in the shared database: only this device counts.
        Assert.DoesNotContain(sender.Sent, sent => sent.Target.Token == device.PushToken);
        var deferred = await DeliveryAsync(user.Id);
        Assert.Equal((NotificationDeliveryStatus.Pending, 0, (DateTimeOffset?)new DateTimeOffset(2026, 11, 15, 4, 30, 0, TimeSpan.Zero)),
            (deferred.Status, deferred.AttemptCount, deferred.NextAttemptAtUtc));

        await TickAsync(new DateTimeOffset(2026, 11, 15, 4, 31, 0, TimeSpan.Zero), sender);   // 10:01 IST

        var sent = await DeliveryAsync(user.Id);
        Assert.Equal((NotificationDeliveryStatus.Sent, 1), (sent.Status, sent.AttemptCount));
        Assert.Equal("A planned expense is due", Assert.Single(sender.Sent, message => message.Target.Token == device.PushToken).Message.Body);
    }

    // ---- Helpers ----

    private async Task TickAsync(DateTimeOffset now, IPushNotificationSender? sender = null)
    {
        await using var scope = fixture.CreateScope();
        var services = scope.ServiceProvider;
        var time = new FixedTimeProvider(now);
        var deliveries = services.GetRequiredService<INotificationDeliveryStore>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var dispatcher = new NotificationDispatcher(
            deliveries, services.GetRequiredService<IDeviceRegistrationRepository>(), Preferences(scope), unitOfWork, time, sender);

        // The production wiring of both handlers, from the scope's real repositories.
        IAutomationHandler[] handlers =
        [
            new RecurringTransactionReminderHandler(Reminders(scope), services.GetRequiredService<IRecurringRepository>(), Preferences(scope), time),
            new PlannedExpenseReminderHandler(Reminders(scope), services.GetRequiredService<IPlannedExpenseRepository>(), Preferences(scope), time)
        ];

        await new RunAutomationTick(handlers, services.GetRequiredService<IAutomationExecutionStore>(), dispatcher, deliveries, unitOfWork, new AutomationTickGuard(), time)
            .RunAsync();
    }

    private async Task<List<Guid>> DueRuleIdsAsync(Guid userId, DateOnly date)
    {
        await using var scope = fixture.CreateScope();
        var found = await Reminders(scope).FindDueRecurringAsync(RecurringTransactionReminderHandler.Type, [new FinanceReminderOpenZone(Zone, date)], 1000, default);

        Assert.All(found, item => Assert.Equal((Zone, date), (item.TimeZoneId, item.LocalDate)));
        return found.Where(item => item.UserId == userId).Select(item => item.RuleId).ToList();
    }

    private async Task<List<Guid>> DuePlannedIdsAsync(Guid userId, DateOnly date)
    {
        await using var scope = fixture.CreateScope();
        var found = await Reminders(scope).FindDuePlannedExpensesAsync(PlannedExpenseReminderHandler.Type, [new FinanceReminderOpenZone(Zone, date)], 1000, default);

        return found.Where(item => item.UserId == userId).Select(item => item.PlannedExpenseId).ToList();
    }

    private async Task<NotificationDelivery> DeliveryAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        return await Db(scope).NotificationDeliveries.AsNoTracking().SingleAsync(row => row.UserId == userId);
    }

    // Rows of every Finance table a reminder could touch.
    private async Task<string> FinanceFingerprintAsync(Guid userId)
    {
        await using var scope = fixture.CreateScope();
        var counts = await Db(scope).Database.SqlQuery<string>($"""
            SELECT concat_ws(',',
                (SELECT count(*) FROM transactions WHERE user_id = {userId}),
                (SELECT count(*) FROM recurring_transaction_rules WHERE user_id = {userId}),
                (SELECT max(updated_at_utc)::text FROM recurring_transaction_rules WHERE user_id = {userId}),
                (SELECT count(*) FROM recurring_transaction_occurrences WHERE user_id = {userId}),
                (SELECT count(*) FROM planned_expenses WHERE user_id = {userId}),
                (SELECT max(updated_at_utc)::text FROM planned_expenses WHERE user_id = {userId}),
                (SELECT count(*) FROM planned_expense_states WHERE user_id = {userId})) AS "Value"
            """).SingleAsync();

        return counts;
    }

    private async Task<(Account Account, Category Category)> FinanceAsync(Guid userId)
    {
        var account = Account.Create(userId, "Bank", AccountType.BankAccount, "EUR", Due.AddDays(-400));
        var category = Category.Create(userId, "Rent", CategoryType.Expense, parent: null, Due.AddDays(-400));
        await PostgresAssert.InsertAsync(fixture, account, category);

        return (account, category);
    }

    private async Task<RecurringTransactionRule> RuleAsync(
        Guid userId, Account account, Category category, int day, TransactionType type = TransactionType.Expense, int startMonth = 1, int? endMonth = null)
    {
        var rule = RecurringTransactionRule.Create(userId, "Rent", type, account.Id, category.Id, 900m, day, 2026, startMonth, null, Due.AddDays(-400),
            endMonth is null ? null : 2026, endMonth);
        await PostgresAssert.InsertAsync(fixture, rule);

        return rule;
    }

    private async Task<PlannedExpense> PlannedAsync(Guid userId, Account account, Category category, DateOnly date)
    {
        var expense = PlannedExpense.Create(userId, "Dentist", account, category, 250m, date, null, Due.AddDays(-30));
        await PostgresAssert.InsertAsync(fixture, expense);

        return expense;
    }

    private async Task<DeviceRegistration> RegisterDeviceAsync(Guid userId)
    {
        var registration = DeviceRegistration.Register(userId, $"install-{Guid.NewGuid():N}", DevicePlatform.Android, $"token-{Guid.NewGuid():N}", true, Due.AddDays(-1));

        await using var scope = fixture.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IDeviceRegistrationRepository>().UpsertAsync(registration, default));

        return await Db(scope).DeviceRegistrations.AsNoTracking().SingleAsync(row => row.UserId == userId);
    }

    private async Task<bool> SetAsync(Guid userId, bool recurring, bool planned, TimeOnly start, TimeOnly end)
    {
        await using var scope = fixture.CreateScope();
        return await Preferences(scope).SetAsync(NotificationPreferences.Create(userId, recurring, planned, QuietHours.Create(start, end), Now), default);
    }

    private async Task<User> NewUserAsync(string? timeZoneId = null)
    {
        var user = User.CreateFromExternalIdentity(null, null, Due.AddDays(-400));

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

    private static INotificationPreferencesRepository Preferences(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<INotificationPreferencesRepository>();

    private static IFinanceReminderRepository Reminders(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IFinanceReminderRepository>();

    private static LifeOSDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();

    private static Task<List<string>> Strings(Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database, string sql) =>
        database.SqlQueryRaw<string>(sql).ToListAsync();
}
