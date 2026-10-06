using LifeOS.Application.Notifications;
using LifeOS.Application.Persistence;
using LifeOS.Domain.Notifications;

namespace LifeOS.UnitTests.Fakes;

// Same preconditions as the PostgreSQL repository/store statements, under one lock. Atomicity under
// concurrency and transaction rollback are proven against PostgreSQL in the integration tests.
internal sealed class InMemoryDeviceRegistrationRepository : IDeviceRegistrationRepository
{
    private readonly Lock _lock = new();

    public List<Row> Rows { get; } = [];

    // Users that exist; null = every user exists.
    public HashSet<Guid>? ExistingUsers { get; set; }

    public sealed class Row
    {
        public required Guid Id { get; init; }
        public required Guid UserId { get; init; }
        public required string InstallationId { get; init; }
        public required DateTimeOffset CreatedAtUtc { get; init; }
        public PushProvider PushProvider { get; set; }
        public string? PushToken { get; set; }
        public DeviceRegistrationStatus Status { get; set; }
        public DeviceInactiveReason? InactiveReason { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public DateTimeOffset LastSeenAtUtc { get; set; }
    }

    public Row Single(Guid userId, string installationId)
    {
        lock (_lock)
        {
            return Rows.Single(row => row.UserId == userId && row.InstallationId == installationId);
        }
    }

    public Task<bool> UpsertAsync(DeviceRegistration registration, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (ExistingUsers is not null && !ExistingUsers.Contains(registration.UserId))
            {
                return Task.FromResult(false);
            }

            foreach (var other in Rows.Where(row => row.InstallationId == registration.InstallationId
                && row.UserId != registration.UserId && row.Status == DeviceRegistrationStatus.Active))
            {
                Deactivate(other, DeviceInactiveReason.SignedOut, registration.UpdatedAtUtc);
            }

            if (registration.PushToken is not null)
            {
                foreach (var other in Rows.Where(row => row.PushProvider == registration.PushProvider && row.PushToken == registration.PushToken
                    && !(row.InstallationId == registration.InstallationId && row.UserId == registration.UserId)))
                {
                    Deactivate(other, DeviceInactiveReason.TokenInvalid, registration.UpdatedAtUtc);
                }
            }

            var own = Rows.SingleOrDefault(row => row.InstallationId == registration.InstallationId && row.UserId == registration.UserId);

            if (own is null)
            {
                own = new Row
                {
                    Id = registration.Id,
                    UserId = registration.UserId,
                    InstallationId = registration.InstallationId,
                    CreatedAtUtc = registration.CreatedAtUtc
                };
                Rows.Add(own);
            }

            own.PushProvider = registration.PushProvider;
            own.PushToken = registration.PushToken;
            own.Status = registration.Status;
            own.InactiveReason = registration.InactiveReason;
            own.UpdatedAtUtc = registration.UpdatedAtUtc;
            own.LastSeenAtUtc = registration.LastSeenAtUtc;

            return Task.FromResult(true);
        }
    }

    public Task<bool> SignOutAsync(Guid userId, string installationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var row = Rows.SingleOrDefault(row => row.UserId == userId && row.InstallationId == installationId);

            if (row is not null)
            {
                Deactivate(row, DeviceInactiveReason.SignedOut, nowUtc);
            }

            return Task.FromResult(row is not null);
        }
    }

    public Task<PushTarget?> GetPushTargetAsync(Guid deviceRegistrationId, Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var row = Rows.SingleOrDefault(row => row.Id == deviceRegistrationId && row.UserId == userId
                && row.Status == DeviceRegistrationStatus.Active && row.PushToken is not null);

            return Task.FromResult(row is null ? null : new PushTarget(row.PushProvider, row.PushToken!));
        }
    }

    public Task<bool> InvalidateTokenAsync(Guid deviceRegistrationId, string pushToken, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var row = Rows.SingleOrDefault(row => row.Id == deviceRegistrationId && row.PushToken == pushToken);

            if (row is not null)
            {
                Deactivate(row, DeviceInactiveReason.TokenInvalid, nowUtc);
            }

            return Task.FromResult(row is not null);
        }
    }

    public IReadOnlyList<Guid> ActiveDeviceIds(Guid userId)
    {
        lock (_lock)
        {
            return Rows.Where(row => row.UserId == userId && row.Status == DeviceRegistrationStatus.Active)
                .OrderBy(row => row.Id)
                .Select(row => row.Id)
                .ToList();
        }
    }

    private static void Deactivate(Row row, DeviceInactiveReason reason, DateTimeOffset nowUtc)
    {
        row.Status = DeviceRegistrationStatus.Inactive;
        row.InactiveReason = reason;
        row.PushToken = null;
        row.UpdatedAtUtc = nowUtc;
    }
}

internal sealed class InMemoryNotificationDeliveryStore(InMemoryDeviceRegistrationRepository devices) : INotificationDeliveryStore
{
    private readonly Lock _lock = new();

    public List<Row> Rows { get; } = [];

    public sealed class Row
    {
        public required Guid Id { get; init; }
        public required Guid UserId { get; init; }
        public required Guid DeviceRegistrationId { get; init; }
        public required string NotificationKey { get; init; }
        public required NotificationType Type { get; init; }
        public Guid? SourceExecutionId { get; init; }
        public string? ResourceType { get; init; }
        public Guid? ResourceId { get; init; }
        public required DateTimeOffset ExpiresAtUtc { get; init; }
        public required DateTimeOffset CreatedAtUtc { get; init; }
        public NotificationDeliveryStatus Status { get; set; }
        public int AttemptCount { get; set; }
        public DateTimeOffset? NextAttemptAtUtc { get; set; }
        public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
        public NotificationErrorCode? LastErrorCode { get; set; }
        public DateTimeOffset? SentAtUtc { get; set; }
    }

    public Task<int> EnqueueAsync(LogicalNotification notification, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var created = 0;

            foreach (var deviceId in devices.ActiveDeviceIds(notification.UserId))
            {
                if (Rows.Any(row => row.NotificationKey == notification.NotificationKey && row.DeviceRegistrationId == deviceId))
                {
                    continue;
                }

                var delivery = NotificationDelivery.Create(
                    notification.UserId, deviceId, notification.NotificationKey, notification.Type, notification.SourceExecutionId,
                    notification.ResourceType, notification.ResourceId, nowUtc, notification.ExpiresAtUtc);

                Rows.Add(new Row
                {
                    Id = delivery.Id,
                    UserId = delivery.UserId,
                    DeviceRegistrationId = delivery.DeviceRegistrationId,
                    NotificationKey = delivery.NotificationKey,
                    Type = delivery.NotificationType,
                    SourceExecutionId = delivery.SourceExecutionId,
                    ResourceType = delivery.ResourceType,
                    ResourceId = delivery.ResourceId,
                    ExpiresAtUtc = delivery.ExpiresAtUtc,
                    CreatedAtUtc = delivery.CreatedAtUtc,
                    Status = NotificationDeliveryStatus.Pending,
                    NextAttemptAtUtc = delivery.NextAttemptAtUtc
                });
                created++;
            }

            return Task.FromResult(created);
        }
    }

    public Task<NotificationDeliveryWorkItem?> ClaimNextAsync(DateTimeOffset nowUtc, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken) =>
        Claim(null, nowUtc, lease, maxAttempts);

    public Task<NotificationDeliveryWorkItem?> ClaimNextForNotificationAsync(
        string notificationKey, DateTimeOffset nowUtc, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken) =>
        Claim(notificationKey, nowUtc, lease, maxAttempts);

    private Task<NotificationDeliveryWorkItem?> Claim(string? notificationKey, DateTimeOffset nowUtc, TimeSpan lease, int maxAttempts)
    {
        lock (_lock)
        {
            var row = Rows
                .Where(row => (notificationKey is null || row.NotificationKey == notificationKey)
                    && nowUtc < row.ExpiresAtUtc
                    && ((row.Status == NotificationDeliveryStatus.Pending && row.NextAttemptAtUtc <= nowUtc)
                        || (row.Status == NotificationDeliveryStatus.Sending && row.LeaseExpiresAtUtc <= nowUtc && row.AttemptCount < maxAttempts)))
                .OrderBy(row => row.NextAttemptAtUtc ?? row.LeaseExpiresAtUtc)
                .ThenBy(row => row.Id)
                .FirstOrDefault();

            if (row is null)
            {
                return Task.FromResult<NotificationDeliveryWorkItem?>(null);
            }

            row.Status = NotificationDeliveryStatus.Sending;
            row.AttemptCount++;
            row.LeaseExpiresAtUtc = nowUtc + lease;
            row.NextAttemptAtUtc = null;

            return Task.FromResult<NotificationDeliveryWorkItem?>(new NotificationDeliveryWorkItem(
                row.Id, row.AttemptCount, row.UserId, row.DeviceRegistrationId, row.NotificationKey, row.Type, row.ResourceType, row.ResourceId, row.ExpiresAtUtc));
        }
    }

    public Task<int> FinalizeAbandonedAsync(DateTimeOffset nowUtc, int maxAttempts, int limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var abandoned = Rows
                .Where(row => (row.Status == NotificationDeliveryStatus.Pending && nowUtc >= row.ExpiresAtUtc)
                    || (row.Status == NotificationDeliveryStatus.Sending && row.LeaseExpiresAtUtc <= nowUtc
                        && (nowUtc >= row.ExpiresAtUtc || row.AttemptCount >= maxAttempts)))
                .OrderBy(row => row.Id)
                .Take(limit)
                .ToList();

            foreach (var row in abandoned)
            {
                row.LastErrorCode = nowUtc >= row.ExpiresAtUtc ? NotificationErrorCode.Expired : NotificationErrorCode.MaxAttempts;
                row.Status = NotificationDeliveryStatus.Failed;
                row.LeaseExpiresAtUtc = null;
                row.NextAttemptAtUtc = null;
            }

            return Task.FromResult(abandoned.Count);
        }
    }

    public Task<bool> CompleteSentAsync(Guid deliveryId, int attempt, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        Complete(deliveryId, attempt, row =>
        {
            row.Status = NotificationDeliveryStatus.Sent;
            row.SentAtUtc = nowUtc;
            row.LastErrorCode = null;
        });

    public Task<bool> CompleteRetryAsync(Guid deliveryId, int attempt, NotificationErrorCode code, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken) =>
        Complete(deliveryId, attempt, row =>
        {
            row.Status = NotificationDeliveryStatus.Pending;
            row.NextAttemptAtUtc = nextAttemptAtUtc;
            row.LastErrorCode = code;
        });

    public Task<bool> CompleteFailedAsync(Guid deliveryId, int attempt, NotificationErrorCode code, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
        Complete(deliveryId, attempt, row =>
        {
            row.Status = NotificationDeliveryStatus.Failed;
            row.LastErrorCode = code;
            row.NextAttemptAtUtc = null;
        });

    public Task<bool> CompleteDeferredAsync(Guid deliveryId, int attempt, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken) =>
        Complete(deliveryId, attempt, row =>
        {
            row.Status = NotificationDeliveryStatus.Pending;
            row.AttemptCount--;
            row.NextAttemptAtUtc = nextAttemptAtUtc;
        });

    private Task<bool> Complete(Guid deliveryId, int attempt, Action<Row> apply)
    {
        lock (_lock)
        {
            var row = Rows.SingleOrDefault(row => row.Id == deliveryId && row.Status == NotificationDeliveryStatus.Sending && row.AttemptCount == attempt);

            if (row is null)
            {
                return Task.FromResult(false);
            }

            apply(row);
            row.LeaseExpiresAtUtc = null;

            return Task.FromResult(true);
        }
    }
}

// Runs the work; records the outcome. Rollback itself is proven against PostgreSQL.
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int Committed { get; private set; }

    public int RolledBack { get; private set; }

    public async Task<bool> TryInTransactionAsync(Func<CancellationToken, Task<bool>> work, CancellationToken cancellationToken)
    {
        var commit = await work(cancellationToken);

        if (commit)
        {
            Committed++;
        }
        else
        {
            RolledBack++;
        }

        return commit;
    }
}

// Test-only push sender: answers with Respond (default Accepted) and records every send.
internal sealed class FakePushNotificationSender : IPushNotificationSender
{
    private readonly Lock _lock = new();

    public List<(PushTarget Target, PushMessage Message)> Sent { get; } = [];

    public Func<PushTarget, PushMessage, Task<PushSendResult>> Respond { get; set; } =
        (_, _) => Task.FromResult(PushSendResult.Accepted);

    public Task<PushSendResult> SendAsync(PushTarget target, PushMessage message, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Sent.Add((target, message));
        }

        return Respond(target, message);
    }
}
