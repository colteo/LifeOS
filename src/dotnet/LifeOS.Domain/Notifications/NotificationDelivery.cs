namespace LifeOS.Domain.Notifications;

// Selects the fixed English copy (PD-3). WeeklyReviewReady is modeled for AUTO-002; nothing sends it yet.
public enum NotificationType
{
    Test,
    WeeklyReviewReady
}

public enum NotificationDeliveryStatus
{
    Pending,
    Sending,
    Sent,
    Failed
}

// AUTO-001 §5.4: the fixed vocabulary of last_error_code. Never provider text.
public enum NotificationErrorCode
{
    Transient,
    TokenInvalid,
    DeviceInactive,
    MaxAttempts,
    Expired,
    Rejected
}

// AUTO-001 §5.4 (PD-7): "did this notification reach this device". One row per (logical
// notification, device registration); the table is the in-database outbox. No title, body, token or
// provider message is stored: the type selects the copy, the token is read from the registration at
// send time. Created Pending with no attempt yet; transitions (claim, lease takeover, fenced
// completion) are atomic statements in the store.
public sealed class NotificationDelivery
{
    public const int MaxNotificationKeyLength = 128;
    public const int MaxResourceTypeLength = 32;

    private NotificationDelivery(
        Guid id,
        Guid userId,
        Guid deviceRegistrationId,
        string notificationKey,
        NotificationType notificationType,
        Guid? sourceExecutionId,
        string? resourceType,
        Guid? resourceId,
        NotificationDeliveryStatus status,
        int attemptCount,
        DateTimeOffset? nextAttemptAtUtc,
        DateTimeOffset? leaseExpiresAtUtc,
        DateTimeOffset expiresAtUtc,
        NotificationErrorCode? lastErrorCode,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? sentAtUtc)
    {
        Id = id;
        UserId = userId;
        DeviceRegistrationId = deviceRegistrationId;
        NotificationKey = notificationKey;
        NotificationType = notificationType;
        SourceExecutionId = sourceExecutionId;
        ResourceType = resourceType;
        ResourceId = resourceId;
        Status = status;
        AttemptCount = attemptCount;
        NextAttemptAtUtc = nextAttemptAtUtc;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        LastErrorCode = lastErrorCode;
        CreatedAtUtc = createdAtUtc;
        SentAtUtc = sentAtUtc;
    }

    public Guid Id { get; }

    public Guid UserId { get; }

    public Guid DeviceRegistrationId { get; }

    // Logical notification identity, deterministic: "automation:<execution id>" | "test:<uuid>".
    public string NotificationKey { get; }

    public NotificationType NotificationType { get; }

    public Guid? SourceExecutionId { get; }

    // Deep-link target opened on tap, e.g. "weekly_review" + the artifact id.
    public string? ResourceType { get; }

    public Guid? ResourceId { get; }

    public NotificationDeliveryStatus Status { get; }

    // Send attempts started; also the fencing token for completion.
    public int AttemptCount { get; }

    // When Pending: the earliest send.
    public DateTimeOffset? NextAttemptAtUtc { get; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; }

    // Never sent at or after this instant.
    public DateTimeOffset ExpiresAtUtc { get; }

    public NotificationErrorCode? LastErrorCode { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    // When the provider accepted the message.
    public DateTimeOffset? SentAtUtc { get; }

    public static NotificationDelivery Create(
        Guid userId,
        Guid deviceRegistrationId,
        string notificationKey,
        NotificationType notificationType,
        Guid? sourceExecutionId,
        string? resourceType,
        Guid? resourceId,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (userId == Guid.Empty || deviceRegistrationId == Guid.Empty)
        {
            throw new ArgumentException("A user and a device registration are required.");
        }

        if (!IsValidNotificationKey(notificationKey))
        {
            throw new ArgumentException("Invalid notification key.", nameof(notificationKey));
        }

        if (resourceType is not null && !IsCode(resourceType, MaxResourceTypeLength))
        {
            throw new ArgumentException("Invalid resource type.", nameof(resourceType));
        }

        var now = nowUtc.ToUniversalTime();
        var expires = expiresAtUtc.ToUniversalTime();

        if (expires <= now)
        {
            throw new ArgumentException("A delivery must expire after it is created.", nameof(expiresAtUtc));
        }

        return new NotificationDelivery(
            Guid.CreateVersion7(),
            userId,
            deviceRegistrationId,
            notificationKey,
            notificationType,
            sourceExecutionId,
            resourceType,
            resourceId,
            NotificationDeliveryStatus.Pending,
            attemptCount: 0,
            nextAttemptAtUtc: now,
            leaseExpiresAtUtc: null,
            expires,
            lastErrorCode: null,
            createdAtUtc: now,
            sentAtUtc: null);
    }

    public static bool IsValidNotificationKey(string? notificationKey) => IsCode(notificationKey, MaxNotificationKeyLength);

    private static bool IsCode(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');
}
