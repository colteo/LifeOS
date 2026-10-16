using LifeOS.Application.Notifications;
using LifeOS.Domain.Notifications;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Notifications;

// REM-001 §6.6: the dispatcher's relevance step. A fake check and the test sender; the order of the
// checks (device → quiet hours → relevance → send), its outcomes, and that types without a check are
// unchanged. Plus the R5 vocabulary: copy, expiry, quiet hours, preferences.
public class NotificationDispatcherRelevanceTests
{
    private static readonly Guid UserA = Guid.Parse("0192f0c3-0000-7000-8000-00000000000a");
    private const string Key = "0192f0e4-11aa-7b20-9f00-6c1d00000007:2026-10-14T20:00";

    // Wed 14 Oct 2026 20:00 CEST (outside the default quiet hours) and 23:00 CEST (inside).
    private static readonly DateTimeOffset Evening = new(2026, 10, 14, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Night = new(2026, 10, 14, 21, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _clock = new(Evening);
    private readonly InMemoryDeviceRegistrationRepository _devices = new();
    private readonly InMemoryNotificationDeliveryStore _deliveries;
    private readonly InMemoryNotificationPreferencesRepository _preferences = new();
    private readonly FakePushNotificationSender _sender = new();
    private readonly FakeRelevanceCheck _check = new();

    public NotificationDispatcherRelevanceTests()
    {
        _deliveries = new InMemoryNotificationDeliveryStore(_devices) { OccurrenceKeyOf = _ => Key };
        _preferences.TimeZones[UserA] = "Europe/Rome";
    }

    private NotificationDispatcher Dispatcher() => new(_deliveries, _devices, _preferences, new FakeUnitOfWork(), _clock, _sender, [_check]);

    private async Task<InMemoryNotificationDeliveryStore.Row> EnqueueAsync(NotificationType type, bool device = true)
    {
        await _devices.UpsertAsync(DeviceRegistration.Register(UserA, "phone-installation-0001", DevicePlatform.Android, device ? "token-a" : null, device,
            _clock.UtcNow), default);
        await _deliveries.EnqueueAsync(
            LogicalNotification.ForAutomation(Guid.CreateVersion7(), UserA, type, "habit", Guid.CreateVersion7(), _clock.UtcNow), _clock.UtcNow, default);

        return _deliveries.Rows.Single();
    }

    [Fact]
    public async Task NoLongerRelevant_IsFailedNotRelevant_AndNothingIsSent()
    {
        var row = await EnqueueAsync(NotificationType.HabitReminder);
        _check.Answer = false;

        Assert.True(await Dispatcher().DispatchNextAsync());

        Assert.Empty(_sender.Sent);
        Assert.Equal((NotificationDeliveryStatus.Failed, (NotificationErrorCode?)NotificationErrorCode.NotRelevant), (row.Status, row.LastErrorCode));
        var context = Assert.Single(_check.Calls);
        Assert.Equal((UserA, NotificationType.HabitReminder, "habit", Key, Evening),
            (context.UserId, context.Type, context.ResourceType, context.SourceOccurrenceKey, context.NowUtc));
    }

    [Fact]
    public async Task StillRelevant_IsSent_OneCheckPerAttempt()
    {
        var row = await EnqueueAsync(NotificationType.HydrationReminder);

        Assert.True(await Dispatcher().DispatchNextAsync());

        var (_, message) = Assert.Single(_sender.Sent);
        Assert.Equal("Your water target for today isn't reached yet", message.Body);
        Assert.Equal(NotificationDeliveryStatus.Sent, row.Status);
        Assert.Single(_check.Calls);
    }

    [Fact]
    public async Task AFailingCheck_IsRetriedLater_NeverSentUnchecked()
    {
        var row = await EnqueueAsync(NotificationType.HabitReminder);
        _check.Throw = true;

        Assert.True(await Dispatcher().DispatchNextAsync());

        Assert.Empty(_sender.Sent);
        Assert.Equal((NotificationDeliveryStatus.Pending, (NotificationErrorCode?)NotificationErrorCode.Transient, (DateTimeOffset?)Evening.AddMinutes(10)),
            (row.Status, row.LastErrorCode, row.NextAttemptAtUtc));

        // The retry is checked again and then sent.
        _check.Throw = false;
        _clock.UtcNow = Evening.AddMinutes(10);
        Assert.True(await Dispatcher().DispatchNextAsync());
        Assert.Single(_sender.Sent);
        Assert.Equal(2, _check.Calls.Count);
    }

    [Theory]
    [InlineData(NotificationType.UserReminder)]
    [InlineData(NotificationType.PlannedExpenseReminder)]
    [InlineData(NotificationType.RecurringTransactionReminder)]
    [InlineData(NotificationType.WeeklyReviewReady)]
    [InlineData(NotificationType.Test)]
    public async Task TypesWithoutACheck_AreUnchanged(NotificationType type)
    {
        await EnqueueAsync(type);
        _check.Answer = false;

        Assert.True(await Dispatcher().DispatchNextAsync());

        Assert.Single(_sender.Sent);
        Assert.Empty(_check.Calls);
    }

    [Fact]
    public async Task QuietHoursComeFirst_ADeferredNudgeIsCheckedWhenReleased()
    {
        // 23:00 CEST with the default quiet hours: deferring to 08:00 outlasts the 3 h expiry, so the nudge
        // expires unchecked and unsent (REM-001 D-10).
        _clock.UtcNow = Night;
        var row = await EnqueueAsync(NotificationType.HabitReminder);

        Assert.True(await Dispatcher().DispatchNextAsync());

        Assert.Equal((NotificationDeliveryStatus.Failed, (NotificationErrorCode?)NotificationErrorCode.Expired), (row.Status, row.LastErrorCode));
        Assert.Empty(_check.Calls);
        Assert.Empty(_sender.Sent);

        // With quiet hours ending inside the 3 h window, the deferred nudge is checked only on release.
        _deliveries.Rows.Clear();
        _preferences.Set(UserA, start: new TimeOnly(23, 0), end: new TimeOnly(0, 30));
        var deferred = await EnqueueAsync(NotificationType.HabitReminder);

        Assert.True(await Dispatcher().DispatchNextAsync());
        Assert.Equal((NotificationDeliveryStatus.Pending, 0), (deferred.Status, deferred.AttemptCount));
        Assert.Empty(_check.Calls);

        _clock.UtcNow = deferred.NextAttemptAtUtc!.Value;
        _check.Answer = false;
        Assert.True(await Dispatcher().DispatchNextAsync());
        Assert.Single(_check.Calls);
        Assert.Equal(NotificationErrorCode.NotRelevant, deferred.LastErrorCode);
    }

    [Fact]
    public async Task AnInactiveDevice_IsNotChecked()
    {
        var row = await EnqueueAsync(NotificationType.HabitReminder);
        await _devices.UpsertAsync(DeviceRegistration.Register(UserA, "phone-installation-0001", DevicePlatform.Android, null, false, Evening), default);

        Assert.True(await Dispatcher().DispatchNextAsync());

        Assert.Equal(NotificationErrorCode.DeviceInactive, row.LastErrorCode);
        Assert.Empty(_check.Calls);
    }

    [Fact]
    public void TwoChecksForOneType_AreAConfigurationError() =>
        Assert.Throws<ArgumentException>(() =>
            new NotificationDispatcher(_deliveries, _devices, _preferences, new FakeUnitOfWork(), _clock, _sender, [_check, new FakeRelevanceCheck()]));

    // ---- R5 vocabulary ----

    [Theory]
    [InlineData(NotificationType.HabitReminder, "A habit is still open today", "habit")]
    [InlineData(NotificationType.HydrationReminder, "Your water target for today isn't reached yet", "hydration")]
    public void NudgeCopy_IsFixed_ExpiresIn3Hours_AndQuietHoursApply(NotificationType type, string body, string dataType)
    {
        var item = new NotificationDeliveryWorkItem(Guid.CreateVersion7(), 1, UserA, Guid.CreateVersion7(), "automation:x", type, null, null, Evening);
        var message = NotificationCatalog.MessageFor(item);

        Assert.Equal(("LifeOS", body, dataType), (message.Title, message.Body, message.Data["type"]));
        Assert.False(message.Data.ContainsKey("id"));
        Assert.Equal(TimeSpan.FromHours(3), NotificationCatalog.ExpiryOf(type));
        Assert.True(NotificationCatalog.IsReminder(type));
    }

    [Fact]
    public void Preferences_HaveHabitAndWaterSwitches_OnByDefault()
    {
        var defaults = NotificationPreferences.Default(UserA);
        Assert.True(defaults.IsEnabled(NotificationType.HabitReminder) && defaults.IsEnabled(NotificationType.HydrationReminder));

        var muted = NotificationPreferences.Create(UserA, true, true, true, QuietHours.Default, Evening, habitRemindersEnabled: false,
            hydrationRemindersEnabled: false);
        Assert.Equal((false, false, true), (muted.IsEnabled(NotificationType.HabitReminder), muted.IsEnabled(NotificationType.HydrationReminder),
            muted.IsEnabled(NotificationType.UserReminder)));
    }

    [Fact]
    public async Task SavingWithoutTheNewSwitches_KeepsTheirSavedValues()
    {
        var handler = new SetNotificationPreferencesHandler(_preferences, new FixedTimeProvider(Evening));
        await handler.HandleAsync(UserA, true, true, true, new TimeOnly(22, 0), new TimeOnly(8, 0), default, habitReminders: false, hydrationReminders: false);

        // Android 1.7.0 sends the form without them.
        await handler.HandleAsync(UserA, false, true, null, new TimeOnly(23, 0), new TimeOnly(7, 0), default);

        var saved = await _preferences.GetAsync(UserA, default);
        Assert.Equal((false, true, false, false), (saved.RecurringTransactionRemindersEnabled, saved.UserRemindersEnabled, saved.HabitRemindersEnabled,
            saved.HydrationRemindersEnabled));

        var view = await new GetNotificationPreferencesHandler(_preferences).HandleAsync(UserA, default);
        Assert.Equal((false, false), (view.HabitReminders, view.HydrationReminders));
    }

    private sealed class FakeRelevanceCheck : INotificationRelevanceCheck
    {
        public bool Answer { get; set; } = true;

        public bool Throw { get; set; }

        public List<NotificationRelevanceContext> Calls { get; } = [];

        public IReadOnlySet<NotificationType> Types { get; } = new HashSet<NotificationType> { NotificationType.HabitReminder, NotificationType.HydrationReminder };

        public Task<bool> IsStillRelevantAsync(NotificationRelevanceContext context, CancellationToken cancellationToken)
        {
            Calls.Add(context);

            return Throw ? throw new InvalidOperationException("database hiccup") : Task.FromResult(Answer);
        }
    }
}
