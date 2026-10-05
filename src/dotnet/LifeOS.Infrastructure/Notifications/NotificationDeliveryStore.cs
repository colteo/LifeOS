using LifeOS.Application.Notifications;
using LifeOS.Domain.Notifications;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Notifications;

// AUTO-001 §5.4 / §8 on PostgreSQL, same patterns as automation_executions:
// - enqueue = INSERT … ON CONFLICT (notification_key, device_registration_id) DO NOTHING;
// - claim/takeover = UPDATE of a row picked with FOR UPDATE SKIP LOCKED (one claimer per row);
// - completion = UPDATE … WHERE status = 'Sending' AND attempt_count = <my attempt> (fencing).
// Every statement runs on the scope's DbContext, so it joins an IUnitOfWork transaction when one is open.
internal sealed class NotificationDeliveryStore(LifeOSDbContext db) : INotificationDeliveryStore
{
    public async Task<int> EnqueueAsync(LogicalNotification notification, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var devices = await db.DeviceRegistrations
            .AsNoTracking()
            .Where(registration => registration.UserId == notification.UserId && registration.Status == DeviceRegistrationStatus.Active)
            .OrderBy(registration => registration.Id)
            .Select(registration => registration.Id)
            .ToListAsync(cancellationToken);

        var created = 0;

        foreach (var deviceRegistrationId in devices)
        {
            var delivery = NotificationDelivery.Create(
                notification.UserId,
                deviceRegistrationId,
                notification.NotificationKey,
                notification.Type,
                notification.SourceExecutionId,
                notification.ResourceType,
                notification.ResourceId,
                nowUtc,
                notification.ExpiresAtUtc);

            created += await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notification_deliveries
                    (id, user_id, device_registration_id, notification_key, notification_type, source_execution_id,
                     resource_type, resource_id, status, attempt_count, next_attempt_at_utc, expires_at_utc, created_at_utc)
                VALUES
                    ({delivery.Id}, {delivery.UserId}, {delivery.DeviceRegistrationId}, {delivery.NotificationKey},
                     {delivery.NotificationType.ToString()}, {delivery.SourceExecutionId}, {delivery.ResourceType}, {delivery.ResourceId},
                     'Pending', 0, {delivery.NextAttemptAtUtc!.Value}, {delivery.ExpiresAtUtc}, {delivery.CreatedAtUtc})
                ON CONFLICT (notification_key, device_registration_id) DO NOTHING
                """, cancellationToken);
        }

        return created;
    }

    public Task<NotificationDeliveryWorkItem?> ClaimNextAsync(DateTimeOffset nowUtc, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken) =>
        ClaimAsync(notificationKey: null, nowUtc, lease, maxAttempts, cancellationToken);

    public Task<NotificationDeliveryWorkItem?> ClaimNextForNotificationAsync(
        string notificationKey, DateTimeOffset nowUtc, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken) =>
        ClaimAsync(notificationKey, nowUtc, lease, maxAttempts, cancellationToken);

    private async Task<NotificationDeliveryWorkItem?> ClaimAsync(
        string? notificationKey, DateTimeOffset nowUtc, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();
        var leaseUntil = now + lease;

        var claimed = await db.NotificationDeliveries
            .FromSqlInterpolated($"""
                UPDATE notification_deliveries
                SET status = 'Sending',
                    attempt_count = attempt_count + 1,
                    lease_expires_at_utc = {leaseUntil},
                    next_attempt_at_utc = NULL
                WHERE id = (
                    SELECT id FROM notification_deliveries
                    WHERE {now} < expires_at_utc
                      AND ({notificationKey}::text IS NULL OR notification_key = {notificationKey})
                      AND ((status = 'Pending' AND next_attempt_at_utc <= {now})
                        OR (status = 'Sending' AND lease_expires_at_utc <= {now} AND attempt_count < {maxAttempts}))
                    ORDER BY COALESCE(next_attempt_at_utc, lease_expires_at_utc), id
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
                RETURNING *
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (claimed.Count == 0)
        {
            return null;
        }

        var delivery = claimed[0];

        return new NotificationDeliveryWorkItem(
            delivery.Id,
            delivery.AttemptCount,
            delivery.UserId,
            delivery.DeviceRegistrationId,
            delivery.NotificationKey,
            delivery.NotificationType,
            delivery.ResourceType,
            delivery.ResourceId,
            delivery.ExpiresAtUtc);
    }

    public Task<int> FinalizeAbandonedAsync(DateTimeOffset nowUtc, int maxAttempts, int limit, CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();

        return db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notification_deliveries
            SET status = 'Failed',
                last_error_code = CASE WHEN {now} >= expires_at_utc THEN {nameof(NotificationErrorCode.Expired)}
                                       ELSE {nameof(NotificationErrorCode.MaxAttempts)} END,
                lease_expires_at_utc = NULL,
                next_attempt_at_utc = NULL
            WHERE id IN (
                SELECT id FROM notification_deliveries
                WHERE (status = 'Pending' AND {now} >= expires_at_utc)
                   OR (status = 'Sending' AND lease_expires_at_utc <= {now}
                       AND ({now} >= expires_at_utc OR attempt_count >= {maxAttempts}))
                ORDER BY id
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED)
            """, cancellationToken);
    }

    public async Task<bool> CompleteSentAsync(Guid deliveryId, int attempt, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var now = nowUtc.ToUniversalTime();

        return await Fenced(deliveryId, attempt).ExecuteUpdateAsync(setters => setters
            .SetProperty(delivery => delivery.Status, NotificationDeliveryStatus.Sent)
            .SetProperty(delivery => delivery.SentAtUtc, now)
            .SetProperty(delivery => delivery.LastErrorCode, (NotificationErrorCode?)null)
            .SetProperty(delivery => delivery.LeaseExpiresAtUtc, (DateTimeOffset?)null),
            cancellationToken) == 1;
    }

    public async Task<bool> CompleteRetryAsync(Guid deliveryId, int attempt, NotificationErrorCode code, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken)
    {
        var next = nextAttemptAtUtc.ToUniversalTime();

        return await Fenced(deliveryId, attempt).ExecuteUpdateAsync(setters => setters
            .SetProperty(delivery => delivery.Status, NotificationDeliveryStatus.Pending)
            .SetProperty(delivery => delivery.NextAttemptAtUtc, next)
            .SetProperty(delivery => delivery.LastErrorCode, code)
            .SetProperty(delivery => delivery.LeaseExpiresAtUtc, (DateTimeOffset?)null),
            cancellationToken) == 1;
    }

    public async Task<bool> CompleteFailedAsync(Guid deliveryId, int attempt, NotificationErrorCode code, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        await Fenced(deliveryId, attempt).ExecuteUpdateAsync(setters => setters
            .SetProperty(delivery => delivery.Status, NotificationDeliveryStatus.Failed)
            .SetProperty(delivery => delivery.LastErrorCode, code)
            .SetProperty(delivery => delivery.LeaseExpiresAtUtc, (DateTimeOffset?)null)
            .SetProperty(delivery => delivery.NextAttemptAtUtc, (DateTimeOffset?)null),
            cancellationToken) == 1;

    private IQueryable<NotificationDelivery> Fenced(Guid deliveryId, int attempt) =>
        db.NotificationDeliveries.Where(delivery => delivery.Id == deliveryId
            && delivery.Status == NotificationDeliveryStatus.Sending
            && delivery.AttemptCount == attempt);
}
