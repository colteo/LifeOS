using LifeOS.Application.Persistence;
using LifeOS.Domain.Notifications;

namespace LifeOS.Application.Notifications;

// AUTO-001 §10: per-device delivery retries.
public static class NotificationDeliveryPolicy
{
    public const int MaxAttempts = 5;

    // At most this many deliveries per tick (Phase A).
    public const int MaxDeliveriesPerTick = 50;

    // A Sending row whose lease expired is a crashed attempt (AUTO-001 §8 lease).
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    // Delay before attempt 2, 3, 4 and 5.
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(3)];

    // A transient failure of `attempt`: retried while attempts remain and the delivery has not
    // expired; otherwise final. NextAttemptAtUtc null means Failed with Code.
    public static (NotificationErrorCode Code, DateTimeOffset? NextAttemptAtUtc) AfterTransientFailure(
        int attempt, DateTimeOffset nowUtc, DateTimeOffset expiresAtUtc)
    {
        if (attempt >= MaxAttempts)
        {
            return (NotificationErrorCode.MaxAttempts, null);
        }

        // Same rule as executions: a Pending row whose next attempt falls after its expiry is made
        // Failed ("Expired") by the Phase A sweep once it expires; it is never sent.
        return nowUtc >= expiresAtUtc
            ? (NotificationErrorCode.Expired, null)
            : (NotificationErrorCode.Transient, nowUtc + RetryDelays[attempt - 1]);
    }
}

// AUTO-001 §11 flow, one delivery at a time: claim → load the registration (still Active?) → build
// the fixed message → send → record the outcome with a fenced completion. Disabled while no
// IPushNotificationSender is registered (WP3A: the FCM sender arrives with WP3B); then nothing is
// claimed, sent or marked.
public sealed class NotificationDispatcher
{
    private readonly INotificationDeliveryStore _deliveries;
    private readonly IDeviceRegistrationRepository _devices;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;
    private readonly IPushNotificationSender? _sender;

    public NotificationDispatcher(
        INotificationDeliveryStore deliveries,
        IDeviceRegistrationRepository devices,
        IUnitOfWork unitOfWork,
        TimeProvider time,
        IPushNotificationSender? sender = null)
    {
        _deliveries = deliveries;
        _devices = devices;
        _unitOfWork = unitOfWork;
        _time = time;
        _sender = sender;
    }

    public bool IsEnabled => _sender is not null;

    public Task<int> FinalizeAbandonedAsync(int limit) =>
        _deliveries.FinalizeAbandonedAsync(_time.GetUtcNow(), NotificationDeliveryPolicy.MaxAttempts, limit, CancellationToken.None);

    // False when no delivery is eligible (or dispatch is disabled). Completion never observes
    // cancellation: a claimed delivery is always recorded.
    public async Task<bool> DispatchNextAsync()
    {
        if (_sender is null)
        {
            return false;
        }

        var item = await _deliveries.ClaimNextAsync(
            _time.GetUtcNow(), NotificationDeliveryPolicy.Lease, NotificationDeliveryPolicy.MaxAttempts, CancellationToken.None);

        if (item is null)
        {
            return false;
        }

        // Signed out, permission denied, token invalid or re-owned: nothing is sent.
        var target = await _devices.GetPushTargetAsync(item.DeviceRegistrationId, item.UserId, CancellationToken.None);

        if (target is null)
        {
            await _deliveries.CompleteFailedAsync(item.DeliveryId, item.Attempt, NotificationErrorCode.DeviceInactive, _time.GetUtcNow(), CancellationToken.None);
            return true;
        }

        PushSendResult result;

        try
        {
            // The attempt is pointless once its lease can be taken over.
            using var lease = new CancellationTokenSource(NotificationDeliveryPolicy.Lease, _time);
            result = await _sender.SendAsync(target, NotificationCatalog.MessageFor(item), lease.Token);
        }
        catch (Exception)
        {
            // Never stored as text: the outcome is only the stable code.
            result = PushSendResult.Transient;
        }

        var nowUtc = _time.GetUtcNow();

        switch (result)
        {
            case PushSendResult.Accepted:
                await _deliveries.CompleteSentAsync(item.DeliveryId, item.Attempt, nowUtc, CancellationToken.None);
                break;

            case PushSendResult.TokenInvalid:
                await _unitOfWork.TryInTransactionAsync(async cancellationToken =>
                {
                    if (!await _deliveries.CompleteFailedAsync(item.DeliveryId, item.Attempt, NotificationErrorCode.TokenInvalid, nowUtc, cancellationToken))
                    {
                        return false;
                    }

                    await _devices.InvalidateTokenAsync(item.DeviceRegistrationId, target.Token, nowUtc, cancellationToken);
                    return true;
                }, CancellationToken.None);
                break;

            case PushSendResult.Rejected:
                await _deliveries.CompleteFailedAsync(item.DeliveryId, item.Attempt, NotificationErrorCode.Rejected, nowUtc, CancellationToken.None);
                break;

            default:
                var (code, nextAttemptAtUtc) = NotificationDeliveryPolicy.AfterTransientFailure(item.Attempt, nowUtc, item.ExpiresAtUtc);

                if (nextAttemptAtUtc is { } next)
                {
                    await _deliveries.CompleteRetryAsync(item.DeliveryId, item.Attempt, code, next, CancellationToken.None);
                }
                else
                {
                    await _deliveries.CompleteFailedAsync(item.DeliveryId, item.Attempt, code, nowUtc, CancellationToken.None);
                }

                break;
        }

        return true;
    }
}
