namespace LifeOS.Application.Notifications;

public enum TestNotificationOutcome
{
    Sent,
    NoActiveDevice,

    // FCM is not configured on this server: nothing is created or sent.
    PushDisabled
}

// Counts only. Failed = deliveries not accepted inline (rejected, invalid token, inactive device, or a
// transient failure that Phase A may still retry within the 15-minute expiry).
public sealed record TestNotificationResult(TestNotificationOutcome Outcome, int Devices, int Sent, int Failed);

// AUTO-001 §12: POST /api/notifications/test. A fixed, harmless message to the caller's own Active
// devices, through the same per-device delivery rows and dispatcher as every notification, sent
// inline so it works as a diagnostic. Nothing comes from the client: no text, token, device or user.
public sealed class SendTestNotificationHandler(INotificationDeliveryStore deliveries, NotificationDispatcher dispatcher, TimeProvider time)
{
    public async Task<TestNotificationResult> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!dispatcher.IsEnabled)
        {
            return new TestNotificationResult(TestNotificationOutcome.PushDisabled, 0, 0, 0);
        }

        var nowUtc = time.GetUtcNow();
        var notification = LogicalNotification.Test(userId, nowUtc);
        var devices = await deliveries.EnqueueAsync(notification, nowUtc, cancellationToken);

        if (devices == 0)
        {
            return new TestNotificationResult(TestNotificationOutcome.NoActiveDevice, 0, 0, 0);
        }

        var (sent, _) = await dispatcher.DispatchNotificationAsync(notification.NotificationKey);

        return new TestNotificationResult(TestNotificationOutcome.Sent, devices, sent, devices - sent);
    }
}
