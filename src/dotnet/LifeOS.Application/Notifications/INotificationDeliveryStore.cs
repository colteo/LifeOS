using LifeOS.Domain.Notifications;

namespace LifeOS.Application.Notifications;

// AUTO-001 §5.4 / §8: notification_deliveries, the in-database outbox. Every transition is one
// atomic statement; correctness never depends on in-memory state. Completion is fenced: it applies
// only while the row is still Sending with the caller's attempt number.
//
// All methods run on the request scope's unit of work, so EnqueueAsync can be composed with a fenced
// automation completion (and, from AUTO-002, the artifact write) in one IUnitOfWork transaction.
public interface INotificationDeliveryStore
{
    // One Pending delivery per currently Active device of the user. Rows that already exist for
    // (notification key, device) are left untouched. Returns the number of rows created.
    Task<int> EnqueueAsync(LogicalNotification notification, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    // Claims the oldest eligible delivery and starts its next attempt (Sending, attempt + 1, lease):
    // Pending whose next attempt is due, or Sending whose lease expired with attempts left
    // (takeover). Never at or after its expiry. Rows locked by a concurrent claimer are skipped.
    Task<NotificationDeliveryWorkItem?> ClaimNextAsync(DateTimeOffset nowUtc, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken);

    // The same claim, limited to one logical notification (the inline test send).
    Task<NotificationDeliveryWorkItem?> ClaimNextForNotificationAsync(
        string notificationKey, DateTimeOffset nowUtc, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken);

    // Makes up to `limit` abandoned rows Failed: Pending past expiry ("Expired"), Sending whose lease
    // expired past expiry ("Expired") or after the last attempt ("MaxAttempts").
    Task<int> FinalizeAbandonedAsync(DateTimeOffset nowUtc, int maxAttempts, int limit, CancellationToken cancellationToken);

    Task<bool> CompleteSentAsync(Guid deliveryId, int attempt, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    // Back to Pending, retried at nextAttemptAtUtc.
    Task<bool> CompleteRetryAsync(Guid deliveryId, int attempt, NotificationErrorCode code, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken);

    Task<bool> CompleteFailedAsync(Guid deliveryId, int attempt, NotificationErrorCode code, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    // AUTO-003A quiet hours: back to Pending until nextAttemptAtUtc without having sent anything. The
    // claim's attempt is given back (attempt_count - 1): a deferral is not a send attempt. The previous
    // error code is kept.
    Task<bool> CompleteDeferredAsync(Guid deliveryId, int attempt, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken);
}

// The claimed attempt of one delivery. Attempt is the fencing token.
public sealed record NotificationDeliveryWorkItem(
    Guid DeliveryId,
    int Attempt,
    Guid UserId,
    Guid DeviceRegistrationId,
    string NotificationKey,
    NotificationType Type,
    string? ResourceType,
    Guid? ResourceId,
    DateTimeOffset ExpiresAtUtc);
