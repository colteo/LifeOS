using LifeOS.Application.Notifications;
using LifeOS.Domain.Notifications;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Notifications;

// AUTO-001 §12: the test notification goes through the same delivery rows and dispatcher, inline,
// only to the caller's own Active devices.
public class TestNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserA = Guid.Parse("0192f0c3-0000-7000-8000-00000000000a");
    private static readonly Guid UserB = Guid.Parse("0192f0c3-0000-7000-8000-00000000000b");

    private readonly FixedTimeProvider _clock = new(Now);
    private readonly InMemoryDeviceRegistrationRepository _devices = new();
    private readonly InMemoryNotificationDeliveryStore _deliveries;
    private readonly FakePushNotificationSender _sender = new();

    public TestNotificationTests()
    {
        _deliveries = new InMemoryNotificationDeliveryStore(_devices);
    }

    [Fact]
    public async Task PushDisabled_CreatesNothing()
    {
        await RegisterAsync(UserA, "phone-installation-0001", "token-a");

        var result = await Handler(sender: null).HandleAsync(UserA, default);

        Assert.Equal(new TestNotificationResult(TestNotificationOutcome.PushDisabled, 0, 0, 0), result);
        Assert.Empty(_deliveries.Rows);
    }

    [Fact]
    public async Task NoActiveDevice_CreatesNothing()
    {
        await RegisterAsync(UserA, "phone-installation-0001", null);
        await RegisterAsync(UserB, "other-installation-01", "token-b");

        var result = await Handler().HandleAsync(UserA, default);

        Assert.Equal(TestNotificationOutcome.NoActiveDevice, result.Outcome);
        Assert.Empty(_deliveries.Rows);
        Assert.Empty(_sender.Sent);
    }

    [Fact]
    public async Task OneDevice_IsSentInline_WithTheFixedTestCopy()
    {
        await RegisterAsync(UserA, "phone-installation-0001", "token-a");

        var result = await Handler().HandleAsync(UserA, default);

        Assert.Equal(new TestNotificationResult(TestNotificationOutcome.Sent, 1, 1, 0), result);
        var (target, message) = Assert.Single(_sender.Sent);
        Assert.Equal("token-a", target.Token);
        Assert.Equal(("LifeOS", "Test notification from LifeOS"), (message.Title, message.Body));
        Assert.Equal(new Dictionary<string, string> { ["type"] = "test" }, message.Data);

        var row = Assert.Single(_deliveries.Rows);
        Assert.StartsWith("test:", row.NotificationKey);
        Assert.Equal(7, Guid.Parse(row.NotificationKey["test:".Length..]).Version);
        Assert.Equal((NotificationType.Test, NotificationDeliveryStatus.Sent, Now.AddMinutes(15)), (row.Type, row.Status, row.ExpiresAtUtc));
    }

    [Fact]
    public async Task MixedOutcomes_AreCounted_AndRetryableRowsStayForPhaseA()
    {
        await RegisterAsync(UserA, "phone-installation-0001", "token-ok");
        await RegisterAsync(UserA, "tablet-installation-01", "token-transient");
        await RegisterAsync(UserA, "watch-installation-001", "token-dead");
        await RegisterAsync(UserB, "other-installation-01", "token-b");
        _sender.Respond = (target, _) => Task.FromResult(target.Token switch
        {
            "token-ok" => PushSendResult.Accepted,
            "token-transient" => PushSendResult.Transient,
            _ => PushSendResult.TokenInvalid
        });

        var result = await Handler().HandleAsync(UserA, default);

        Assert.Equal(new TestNotificationResult(TestNotificationOutcome.Sent, 3, 1, 2), result);
        Assert.DoesNotContain(_sender.Sent, sent => sent.Target.Token == "token-b");
        Assert.Equal(
            [NotificationDeliveryStatus.Pending, NotificationDeliveryStatus.Sent, NotificationDeliveryStatus.Failed],
            _deliveries.Rows.Select(row => row.Status).Order());
        Assert.Equal(DeviceRegistrationStatus.Inactive, _devices.Single(UserA, "watch-installation-001").Status);
    }

    [Fact]
    public async Task InlineDispatch_TouchesOnlyItsOwnNotification()
    {
        await RegisterAsync(UserA, "phone-installation-0001", "token-a");
        await _deliveries.EnqueueAsync(LogicalNotification.Test(UserA, Now), Now, default);

        await Handler().HandleAsync(UserA, default);

        Assert.Single(_sender.Sent);
        Assert.Single(_deliveries.Rows, row => row.Status == NotificationDeliveryStatus.Pending);
    }

    [Fact]
    public void WeeklyReviewReady_IsModeledWithItsFixedCopy()
    {
        var item = new NotificationDeliveryWorkItem(Guid.CreateVersion7(), 1, UserA, Guid.CreateVersion7(), "automation:x", NotificationType.WeeklyReviewReady, null, null, Now);

        var message = NotificationCatalog.MessageFor(item);

        Assert.Equal(("LifeOS", "Your weekly review is ready"), (message.Title, message.Body));
        Assert.Equal("weekly_review", message.Data["type"]);
        Assert.Equal(TimeSpan.FromHours(24), NotificationCatalog.ExpiryOf(NotificationType.WeeklyReviewReady));
    }

    private SendTestNotificationHandler Handler() => Handler(_sender);

    private SendTestNotificationHandler Handler(IPushNotificationSender? sender) =>
        new(_deliveries, new NotificationDispatcher(_deliveries, _devices, new InMemoryNotificationPreferencesRepository(), new FakeUnitOfWork(), _clock, sender), _clock);

    private Task<bool> RegisterAsync(Guid userId, string installationId, string? token) =>
        _devices.UpsertAsync(DeviceRegistration.Register(userId, installationId, DevicePlatform.Android, token, token is not null, Now), default);
}
