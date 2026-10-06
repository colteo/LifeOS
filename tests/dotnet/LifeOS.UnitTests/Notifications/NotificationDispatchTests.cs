using LifeOS.Application.Automation;
using LifeOS.Application.Notifications;
using LifeOS.Domain.Automation;
using LifeOS.Domain.Notifications;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Notifications;

// AUTO-001 WP3A: delivery state machine (Phase A), fan-out of a succeeded execution's notification,
// and the disabled-dispatch behaviour without a push provider. In-memory store and a test-only
// sender; PostgreSQL atomicity and concurrency are proven in LifeOS.IntegrationTests.
public class NotificationDispatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 18, 3, 0, TimeSpan.Zero);
    private static readonly Guid UserA = Guid.Parse("0192f0c3-0000-7000-8000-00000000000a");
    private static readonly Guid UserB = Guid.Parse("0192f0c3-0000-7000-8000-00000000000b");
    private const string Phone = "phone-installation-0001";
    private const string Tablet = "tablet-installation-0001";

    private readonly ManualTimeProvider _clock = new(Start);
    private readonly InMemoryAutomationExecutionStore _executions = new();
    private readonly InMemoryDeviceRegistrationRepository _devices = new();
    private readonly InMemoryNotificationDeliveryStore _deliveries;
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly InMemoryNotificationPreferencesRepository _preferences = new();
    private readonly FakePushNotificationSender _sender = new();

    public NotificationDispatchTests()
    {
        _deliveries = new InMemoryNotificationDeliveryStore(_devices);
    }

    // ---- Phase A disabled without a provider ----

    [Fact]
    public async Task WithoutASender_PhaseAClaimsNothing_AndMarksNothing()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        await EnqueueTestAsync(UserA);

        var result = await Tick(sender: null).RunAsync();

        Assert.Equal(0, result.Deliveries);
        var row = Assert.Single(_deliveries.Rows);
        Assert.Equal((NotificationDeliveryStatus.Pending, 0), (row.Status, row.AttemptCount));
        Assert.Null(row.SentAtUtc);
        Assert.False(Dispatcher(sender: null).IsEnabled);
        Assert.False(await Dispatcher(sender: null).DispatchNextAsync());
    }

    // ---- Fan-out ----

    [Fact]
    public async Task Enqueue_CreatesOneDeliveryPerActiveDevice_Idempotently()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        await RegisterAsync(UserA, Tablet, "token-b");
        await RegisterAsync(UserA, "old-installation-0001", null);
        await RegisterAsync(UserB, "b-installation-00001", "token-c");
        var notification = LogicalNotification.Test(UserA, Start);

        Assert.Equal(2, await _deliveries.EnqueueAsync(notification, Start, default));
        Assert.Equal(0, await _deliveries.EnqueueAsync(notification, Start, default));

        Assert.Equal(2, _deliveries.Rows.Count);
        Assert.All(_deliveries.Rows, row => Assert.Equal((UserA, NotificationDeliveryStatus.Pending, 0, Start, Start.AddMinutes(15)),
            (row.UserId, row.Status, row.AttemptCount, row.NextAttemptAtUtc!.Value, row.ExpiresAtUtc)));
    }

    [Fact]
    public async Task SucceededExecutionWithNotification_EnqueuesItsDeliveries_InTheCompletionTransaction()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        await RegisterAsync(UserA, Tablet, "token-b");
        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Start.AddMinutes(-3));
        var resultId = Guid.CreateVersion7();
        handler.Execute = _ => Task.FromResult(AutomationResult.Succeeded(resultId, new AutomationNotification(NotificationType.Test, "test_resource", resultId)));

        await Tick(handler).RunAsync();

        var execution = Assert.Single(_executions.Rows);
        Assert.Equal(AutomationExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(1, _unitOfWork.Committed);
        Assert.Equal(2, _deliveries.Rows.Count);
        Assert.All(_deliveries.Rows, row =>
        {
            Assert.Equal("automation:" + execution.Id.ToString("D"), row.NotificationKey);
            Assert.Equal((execution.Id, "test_resource", resultId), (row.SourceExecutionId, row.ResourceType, row.ResourceId!.Value));
        });
    }

    [Fact]
    public async Task FencedOutSuccess_EnqueuesNothing()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Start.AddMinutes(-3));

        // Attempt 1 outlives its lease; another instance takes over and succeeds without a notification.
        handler.Execute = async occurrence =>
        {
            if (occurrence.Attempt == 1)
            {
                _clock.Advance(AutomationExecutionPolicy.Lease);
                await Tick(handler, guard: new AutomationTickGuard()).RunAsync();
                return AutomationResult.Succeeded(null, new AutomationNotification(NotificationType.Test));
            }

            return AutomationResult.Succeeded();
        };

        await Tick(handler).RunAsync();

        Assert.Empty(_deliveries.Rows);
        Assert.Equal(1, _unitOfWork.RolledBack);
        Assert.Equal(2, Assert.Single(_executions.Rows).AttemptCount);
    }

    [Fact]
    public async Task SucceededWithoutNotification_EnqueuesNothing()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Start.AddMinutes(-3));

        await Tick(handler).RunAsync();

        Assert.Empty(_deliveries.Rows);
    }

    // ---- Delivery state machine ----

    [Fact]
    public async Task Accepted_IsSent_WithTheFixedCopyAndOpaqueData()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        var notification = LogicalNotification.Test(UserA, Start);
        await _deliveries.EnqueueAsync(notification, Start, default);

        var result = await Tick().RunAsync();

        Assert.Equal(1, result.Deliveries);
        var (target, message) = Assert.Single(_sender.Sent);
        Assert.Equal((PushProvider.Fcm, "token-a"), (target.Provider, target.Token));
        Assert.Equal(("LifeOS", "Test notification from LifeOS", notification.NotificationKey), (message.Title, message.Body, message.Tag));
        Assert.Equal(new Dictionary<string, string> { ["type"] = "test" }, message.Data);

        var row = Assert.Single(_deliveries.Rows);
        Assert.Equal((NotificationDeliveryStatus.Sent, 1, Start), (row.Status, row.AttemptCount, row.SentAtUtc!.Value));
        Assert.Null(row.LastErrorCode);
        Assert.Null(row.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task Transient_IsRetriedAfterTheFixedDelays_ThenFailsAtMaxAttempts()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        await _deliveries.EnqueueAsync(Notification(UserA, TimeSpan.FromDays(1)), Start, default);
        _sender.Respond = (_, _) => Task.FromResult(PushSendResult.Transient);
        var dispatcher = Dispatcher();
        var row = Assert.Single(_deliveries.Rows);

        Assert.True(await dispatcher.DispatchNextAsync());
        Assert.Equal((NotificationDeliveryStatus.Pending, 1, NotificationErrorCode.Transient, Start.AddMinutes(10)),
            (row.Status, row.AttemptCount, row.LastErrorCode, row.NextAttemptAtUtc!.Value));

        // Not before it is due.
        _clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(await dispatcher.DispatchNextAsync());

        foreach (var delay in new[] { 1, 30, 60, 180 })
        {
            _clock.Advance(TimeSpan.FromMinutes(delay));
            Assert.True(await dispatcher.DispatchNextAsync());
        }

        Assert.Equal((NotificationDeliveryStatus.Failed, 5, NotificationErrorCode.MaxAttempts), (row.Status, row.AttemptCount, row.LastErrorCode));
        Assert.Equal(5, _sender.Sent.Count);

        _clock.Advance(TimeSpan.FromHours(6));
        Assert.False(await dispatcher.DispatchNextAsync());
    }

    [Fact]
    public async Task SenderException_IsTransient()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        await EnqueueTestAsync(UserA);
        _sender.Respond = (_, _) => throw new HttpRequestException("provider said: token-a is bad");

        await Dispatcher().DispatchNextAsync();

        Assert.Equal((NotificationDeliveryStatus.Pending, NotificationErrorCode.Transient), (_deliveries.Rows[0].Status, _deliveries.Rows[0].LastErrorCode));
    }

    [Fact]
    public async Task TokenInvalid_FailsTheDelivery_AndDeactivatesTheRegistration()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        await RegisterAsync(UserA, Tablet, "token-b");
        await EnqueueTestAsync(UserA);
        _sender.Respond = (target, _) => Task.FromResult(target.Token == "token-a" ? PushSendResult.TokenInvalid : PushSendResult.Accepted);

        await Tick().RunAsync();

        var phone = _devices.Single(UserA, Phone);
        Assert.Equal((DeviceRegistrationStatus.Inactive, DeviceInactiveReason.TokenInvalid), (phone.Status, phone.InactiveReason!.Value));
        Assert.Null(phone.PushToken);
        Assert.Equal(DeviceRegistrationStatus.Active, _devices.Single(UserA, Tablet).Status);
        Assert.Equal(
            [(phone.Id, NotificationDeliveryStatus.Failed, (NotificationErrorCode?)NotificationErrorCode.TokenInvalid)],
            _deliveries.Rows.Where(row => row.Status != NotificationDeliveryStatus.Sent).Select(row => (row.DeviceRegistrationId, row.Status, row.LastErrorCode)));
        Assert.Single(_deliveries.Rows, row => row.Status == NotificationDeliveryStatus.Sent);
    }

    [Fact]
    public async Task Rejected_FailsWithoutRetry()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        await EnqueueTestAsync(UserA);
        _sender.Respond = (_, _) => Task.FromResult(PushSendResult.Rejected);

        await Dispatcher().DispatchNextAsync();
        _clock.Advance(TimeSpan.FromHours(1));

        Assert.False(await Dispatcher().DispatchNextAsync());
        Assert.Equal((NotificationDeliveryStatus.Failed, NotificationErrorCode.Rejected), (_deliveries.Rows[0].Status, _deliveries.Rows[0].LastErrorCode));
        Assert.Equal(DeviceRegistrationStatus.Active, _devices.Single(UserA, Phone).Status);
    }

    [Fact]
    public async Task DeviceSignedOutAfterEnqueue_FailsAsDeviceInactive_WithoutSending()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        await EnqueueTestAsync(UserA);
        await _devices.SignOutAsync(UserA, Phone, Start, default);

        await Dispatcher().DispatchNextAsync();

        Assert.Empty(_sender.Sent);
        Assert.Equal((NotificationDeliveryStatus.Failed, NotificationErrorCode.DeviceInactive), (_deliveries.Rows[0].Status, _deliveries.Rows[0].LastErrorCode));
    }

    // A's pending delivery on a re-owned installation never reaches B's token.
    [Fact]
    public async Task InstallationReownedByAnotherUser_PreviousOwnersDeliveryIsNotSentToTheNewOwner()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        await EnqueueTestAsync(UserA);
        await RegisterAsync(UserB, Phone, "token-b");

        await Tick().RunAsync();

        Assert.Empty(_sender.Sent);
        var row = Assert.Single(_deliveries.Rows);
        Assert.Equal((UserA, _devices.Single(UserA, Phone).Id, NotificationErrorCode.DeviceInactive), (row.UserId, row.DeviceRegistrationId, row.LastErrorCode!.Value));
    }

    [Fact]
    public async Task ExpiredDelivery_IsFailedExpired_WithoutSending()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        await EnqueueTestAsync(UserA);

        _clock.Advance(TimeSpan.FromMinutes(15));
        await Tick().RunAsync();

        Assert.Empty(_sender.Sent);
        Assert.Equal((NotificationDeliveryStatus.Failed, NotificationErrorCode.Expired), (_deliveries.Rows[0].Status, _deliveries.Rows[0].LastErrorCode));
    }

    [Fact]
    public async Task StaleSendingLease_IsTakenOver_AndTheOldAttemptIsFencedOut()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        await EnqueueTestAsync(UserA);
        var crashed = (await _deliveries.ClaimNextAsync(Start, NotificationDeliveryPolicy.Lease, NotificationDeliveryPolicy.MaxAttempts, default))!;

        _clock.Advance(NotificationDeliveryPolicy.Lease - TimeSpan.FromSeconds(1));
        Assert.False(await Dispatcher().DispatchNextAsync());

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await Dispatcher().DispatchNextAsync());

        Assert.Equal((NotificationDeliveryStatus.Sent, 2), (_deliveries.Rows[0].Status, _deliveries.Rows[0].AttemptCount));
        Assert.False(await _deliveries.CompleteFailedAsync(crashed.DeliveryId, crashed.Attempt, NotificationErrorCode.Rejected, _clock.UtcNow, default));
        Assert.False(await _deliveries.CompleteSentAsync(crashed.DeliveryId, crashed.Attempt, _clock.UtcNow, default));
    }

    // ---- Phase A in the tick ----

    [Fact]
    public async Task PhaseA_RunsBeforeRetriesAndDiscovery_OldestFirst()
    {
        await RegisterAsync(UserA, Phone, "token-a");
        var order = new List<string>();
        _sender.Respond = (_, message) => { order.Add("delivery:" + message.Tag); return Task.FromResult(PushSendResult.Accepted); };
        var first = LogicalNotification.Test(UserA, Start);
        await _deliveries.EnqueueAsync(first, Start, default);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var second = LogicalNotification.Test(UserA, _clock.UtcNow);
        await _deliveries.EnqueueAsync(second, _clock.UtcNow, default);

        var handler = new TestAutomationHandler();
        handler.AddDue(UserA, "2026-10-04", Start.AddMinutes(-3));
        handler.Execute = _ => { order.Add("execution"); return Task.FromResult(AutomationResult.Succeeded()); };

        var result = await Tick(handler).RunAsync();

        Assert.Equal(["delivery:" + first.NotificationKey, "delivery:" + second.NotificationKey, "execution"], order);
        Assert.Equal((2, 1, false), (result.Deliveries, result.Executions, result.More));
    }

    [Fact]
    public async Task PhaseA_SendsAtMost50PerTick_ThenMore()
    {
        await RegisterAsync(UserA, Phone, "token-a");

        for (var index = 0; index < 55; index++)
        {
            await EnqueueTestAsync(UserA);
        }

        var first = await Tick().RunAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await Tick().RunAsync();

        Assert.Equal((50, true), (first.Deliveries, first.More));
        Assert.Equal((5, false), (second.Deliveries, second.More));
        Assert.All(_deliveries.Rows, row => Assert.Equal(NotificationDeliveryStatus.Sent, row.Status));
    }

    [Fact]
    public async Task PhaseA_StopsClaimingAtTheTimeBudget()
    {
        await RegisterAsync(UserA, Phone, "token-a");

        for (var index = 0; index < 5; index++)
        {
            await EnqueueTestAsync(UserA);
        }

        _sender.Respond = (_, _) =>
        {
            _clock.Advance(TimeSpan.FromSeconds(15));
            return Task.FromResult(PushSendResult.Accepted);
        };

        var result = await Tick().RunAsync();

        Assert.Equal((2, 0, true), (result.Deliveries, result.Executions, result.More));
    }

    // ---- Policy and catalog ----

    [Fact]
    public void Policy_UsesTheDesignValues()
    {
        Assert.Equal(5, NotificationDeliveryPolicy.MaxAttempts);
        Assert.Equal(50, NotificationDeliveryPolicy.MaxDeliveriesPerTick);
        Assert.Equal(TimeSpan.FromMinutes(5), NotificationDeliveryPolicy.Lease);
        Assert.Equal([TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(3)], NotificationDeliveryPolicy.RetryDelays);
        Assert.Equal(TimeSpan.FromMinutes(15), NotificationCatalog.ExpiryOf(NotificationType.Test));
    }

    [Fact]
    public void LogicalNotificationKeys_AreDeterministicForAutomations()
    {
        var executionId = Guid.CreateVersion7();

        var notification = LogicalNotification.ForAutomation(executionId, UserA, NotificationType.Test, "weekly_review", executionId, Start);

        Assert.Equal("automation:" + executionId.ToString("D"), notification.NotificationKey);
        Assert.Equal(notification, LogicalNotification.ForAutomation(executionId, UserA, NotificationType.Test, "weekly_review", executionId, Start));
        Assert.StartsWith("test:", LogicalNotification.Test(UserA, Start).NotificationKey);
        Assert.NotEqual(LogicalNotification.Test(UserA, Start).NotificationKey, LogicalNotification.Test(UserA, Start).NotificationKey);
    }

    [Fact]
    public void Message_CarriesOnlyTypeAndOpaqueId()
    {
        var resourceId = Guid.CreateVersion7();
        var item = new NotificationDeliveryWorkItem(Guid.CreateVersion7(), 1, UserA, Guid.CreateVersion7(), "automation:x", NotificationType.Test, "weekly_review", resourceId, Start);

        var message = NotificationCatalog.MessageFor(item);

        Assert.Equal(new Dictionary<string, string> { ["type"] = "weekly_review", ["id"] = resourceId.ToString("D") }, message.Data);
        Assert.Equal("automation:x", message.Tag);
    }

    [Fact]
    public void PushTarget_NeverPrintsItsToken()
    {
        Assert.DoesNotContain("secret-token", new PushTarget(PushProvider.Fcm, "secret-token").ToString());
    }

    // ---- Quiet hours (AUTO-003A) ----

    // 2026-10-04 23:00 CEST: inside the default quiet hours (22:00–08:00) in Europe/Rome.
    private static readonly DateTimeOffset RomeNight = new(2026, 10, 4, 21, 0, 0, TimeSpan.Zero);

    // 2026-10-05 08:00 CEST.
    private static readonly DateTimeOffset RomeMorning = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(NotificationType.RecurringTransactionReminder)]
    [InlineData(NotificationType.PlannedExpenseReminder)]
    public async Task Reminder_InsideQuietHours_IsDeferredToTheirEnd_WithoutUsingAnAttempt(NotificationType type)
    {
        _clock.UtcNow = RomeNight;
        _preferences.TimeZones[UserA] = "Europe/Rome";
        await RegisterAsync(UserA, Phone, "token-a");
        await EnqueueReminderAsync(UserA, type);

        Assert.True(await Dispatcher().DispatchNextAsync());

        Assert.Empty(_sender.Sent);
        var row = Assert.Single(_deliveries.Rows);
        Assert.Equal((NotificationDeliveryStatus.Pending, 0, (DateTimeOffset?)RomeMorning), (row.Status, row.AttemptCount, row.NextAttemptAtUtc));
        Assert.False(await Dispatcher().DispatchNextAsync());

        _clock.UtcNow = RomeMorning;
        Assert.True(await Dispatcher().DispatchNextAsync());

        var (_, message) = Assert.Single(_sender.Sent);
        Assert.Equal("LifeOS", message.Title);
        Assert.Equal((NotificationDeliveryStatus.Sent, 1), (row.Status, row.AttemptCount));
    }

    [Fact]
    public async Task Reminder_UsesTheUsersOwnQuietHours()
    {
        // 09:00–10:00 local; at 09:30 CEST (07:30Z) the reminder waits until 10:00 CEST (08:00Z).
        _clock.UtcNow = new DateTimeOffset(2026, 10, 5, 7, 30, 0, TimeSpan.Zero);
        _preferences.TimeZones[UserA] = "Europe/Rome";
        _preferences.Set(UserA, start: new TimeOnly(9, 0), end: new TimeOnly(10, 0));
        await RegisterAsync(UserA, Phone, "token-a");
        await EnqueueReminderAsync(UserA, NotificationType.RecurringTransactionReminder);

        await Dispatcher().DispatchNextAsync();

        Assert.Empty(_sender.Sent);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero), Assert.Single(_deliveries.Rows).NextAttemptAtUtc);
    }

    [Fact]
    public async Task Reminder_OutsideQuietHours_IsSentAtOnce()
    {
        _clock.UtcNow = RomeMorning;
        _preferences.TimeZones[UserA] = "Europe/Rome";
        await RegisterAsync(UserA, Phone, "token-a");
        await EnqueueReminderAsync(UserA, NotificationType.PlannedExpenseReminder);

        await Dispatcher().DispatchNextAsync();

        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task WeeklyReviewAndTest_AreNeverHeldBackByQuietHours()
    {
        _clock.UtcNow = RomeNight;
        _preferences.TimeZones[UserA] = "Europe/Rome";
        await RegisterAsync(UserA, Phone, "token-a");
        await EnqueueTestAsync(UserA);
        await _deliveries.EnqueueAsync(
            LogicalNotification.ForAutomation(Guid.CreateVersion7(), UserA, NotificationType.WeeklyReviewReady, "weekly_review", Guid.CreateVersion7(), RomeNight),
            RomeNight, default);

        await Dispatcher().DispatchNextAsync();
        await Dispatcher().DispatchNextAsync();

        Assert.Equal(2, _sender.Sent.Count);
    }

    [Fact]
    public async Task Reminder_WhoseQuietHoursOutlastItsExpiry_IsExpired_NotSent()
    {
        _clock.UtcNow = RomeNight;
        _preferences.TimeZones[UserA] = "Europe/Rome";
        await RegisterAsync(UserA, Phone, "token-a");
        await _deliveries.EnqueueAsync(
            Reminder(UserA, NotificationType.RecurringTransactionReminder) with { ExpiresAtUtc = RomeNight.AddHours(1) }, RomeNight, default);

        await Dispatcher().DispatchNextAsync();

        Assert.Empty(_sender.Sent);
        Assert.Equal((NotificationDeliveryStatus.Failed, (NotificationErrorCode?)NotificationErrorCode.Expired),
            (Assert.Single(_deliveries.Rows).Status, _deliveries.Rows[0].LastErrorCode));
    }

    [Fact]
    public async Task Reminder_ForAUserWithoutATimeZone_IsSent()
    {
        _clock.UtcNow = RomeNight;
        await RegisterAsync(UserA, Phone, "token-a");
        await EnqueueReminderAsync(UserA, NotificationType.RecurringTransactionReminder);

        await Dispatcher().DispatchNextAsync();

        Assert.Single(_sender.Sent);
    }

    [Fact]
    public async Task Reminder_RetriedAfterATransientFailure_IntoQuietHours_IsDeferredAgain()
    {
        // 21:55 CEST: first attempt fails transiently; the retry (10 min later) falls in quiet hours.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 4, 19, 55, 0, TimeSpan.Zero);
        _preferences.TimeZones[UserA] = "Europe/Rome";
        await RegisterAsync(UserA, Phone, "token-a");
        await EnqueueReminderAsync(UserA, NotificationType.RecurringTransactionReminder);
        _sender.Respond = (_, _) => Task.FromResult(PushSendResult.Transient);

        await Dispatcher().DispatchNextAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        await Dispatcher().DispatchNextAsync();

        var row = Assert.Single(_deliveries.Rows);
        Assert.Single(_sender.Sent);
        Assert.Equal((NotificationDeliveryStatus.Pending, 1, (DateTimeOffset?)RomeMorning), (row.Status, row.AttemptCount, row.NextAttemptAtUtc));
    }

    [Theory]
    [InlineData(NotificationType.RecurringTransactionReminder, "A recurring transaction needs your confirmation", "finance_recurring")]
    [InlineData(NotificationType.PlannedExpenseReminder, "A planned expense is due", "finance_planned_expense")]
    public void ReminderCopy_IsFixedAndCarriesOnlyTypeAndOpaqueId(NotificationType type, string body, string dataType)
    {
        var resourceId = Guid.CreateVersion7();
        var item = new NotificationDeliveryWorkItem(Guid.CreateVersion7(), 1, UserA, Guid.CreateVersion7(), "automation:x", type, dataType, resourceId, Start);

        var message = NotificationCatalog.MessageFor(item);

        Assert.Equal(("LifeOS", body), (message.Title, message.Body));
        Assert.Equal(new Dictionary<string, string> { ["type"] = dataType, ["id"] = resourceId.ToString("D") }, message.Data);
        Assert.DoesNotMatch(@"\d", message.Body);
        Assert.True(NotificationCatalog.IsReminder(type));
        Assert.Equal(TimeSpan.FromHours(24), NotificationCatalog.ExpiryOf(type));
    }

    private static LogicalNotification Reminder(Guid userId, NotificationType type) =>
        LogicalNotification.ForAutomation(Guid.CreateVersion7(), userId, type, "finance_recurring", Guid.CreateVersion7(), RomeNight);

    private Task EnqueueReminderAsync(Guid userId, NotificationType type) =>
        _deliveries.EnqueueAsync(
            LogicalNotification.ForAutomation(Guid.CreateVersion7(), userId, type, "finance_recurring", Guid.CreateVersion7(), _clock.UtcNow),
            _clock.UtcNow, default);

    private Task EnqueueTestAsync(Guid userId) => _deliveries.EnqueueAsync(LogicalNotification.Test(userId, _clock.UtcNow), _clock.UtcNow, default);

    private static LogicalNotification Notification(Guid userId, TimeSpan expiresIn) =>
        LogicalNotification.Test(userId, Start) with { ExpiresAtUtc = Start + expiresIn };

    private Task<bool> RegisterAsync(Guid userId, string installationId, string? token) =>
        _devices.UpsertAsync(DeviceRegistration.Register(userId, installationId, DevicePlatform.Android, token, token is not null, _clock.UtcNow), default);

    private NotificationDispatcher Dispatcher() => Dispatcher(_sender);

    private NotificationDispatcher Dispatcher(IPushNotificationSender? sender) => new(_deliveries, _devices, _preferences, _unitOfWork, _clock, sender);

    private RunAutomationTick Tick(params IAutomationHandler[] handlers) => Tick(handlers, _sender, new AutomationTickGuard());

    private RunAutomationTick Tick(IAutomationHandler handler, AutomationTickGuard guard) => Tick([handler], _sender, guard);

    private RunAutomationTick Tick(IPushNotificationSender? sender) => Tick([], sender, new AutomationTickGuard());

    private RunAutomationTick Tick(IAutomationHandler[] handlers, IPushNotificationSender? sender, AutomationTickGuard guard) =>
        new(handlers, _executions, Dispatcher(sender), _deliveries, _unitOfWork, guard, _clock);
}
