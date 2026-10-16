using LifeOS.Application.Persistence;
using LifeOS.Application.Users.SetTimeZone;
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
// IPushNotificationSender is registered (FCM not configured); then nothing is claimed, sent or marked.
//
// AUTO-003A: a reminder claimed inside the user's quiet hours (local time in the user's current zone)
// is not sent: it goes back to Pending until the quiet hours end, without using up a send attempt. This
// covers every way a reminder can reach a device (first send, retry, lease takeover). Other types are
// never held back.
//
// REM-001 §6.6: after the quiet-hours step, a delivery whose type has a registered
// INotificationRelevanceCheck is checked once per attempt; no longer relevant → Failed (NotRelevant),
// nothing sent; a failing check → the transient retry path. Types without a check are unchanged.
public sealed class NotificationDispatcher
{
    private readonly INotificationDeliveryStore _deliveries;
    private readonly IDeviceRegistrationRepository _devices;
    private readonly INotificationPreferencesRepository _preferences;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;
    private readonly IPushNotificationSender? _sender;
    private readonly IReadOnlyDictionary<NotificationType, INotificationRelevanceCheck> _relevanceChecks;

    public NotificationDispatcher(
        INotificationDeliveryStore deliveries,
        IDeviceRegistrationRepository devices,
        INotificationPreferencesRepository preferences,
        IUnitOfWork unitOfWork,
        TimeProvider time,
        IPushNotificationSender? sender = null,
        IEnumerable<INotificationRelevanceCheck>? relevanceChecks = null)
    {
        _deliveries = deliveries;
        _devices = devices;
        _preferences = preferences;
        _unitOfWork = unitOfWork;
        _time = time;
        _sender = sender;
        // At most one check per type: a second registration for a type is a composition error.
        _relevanceChecks = (relevanceChecks ?? [])
            .SelectMany(check => check.Types.Select(type => (Type: type, Check: check)))
            .ToDictionary(entry => entry.Type, entry => entry.Check);
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

        await ProcessAsync(_sender, item);
        return true;
    }

    // Sends every currently due delivery of one logical notification now (the test notification).
    // Rows another dispatcher is already sending are left to it. Returns how many were accepted by the
    // provider and how many were attempted without being accepted (those may still be retried).
    public async Task<(int Sent, int NotSent)> DispatchNotificationAsync(string notificationKey)
    {
        if (_sender is null)
        {
            return (0, 0);
        }

        var (sent, notSent) = (0, 0);

        while (await _deliveries.ClaimNextForNotificationAsync(
            notificationKey, _time.GetUtcNow(), NotificationDeliveryPolicy.Lease, NotificationDeliveryPolicy.MaxAttempts, CancellationToken.None) is { } item)
        {
            if (await ProcessAsync(_sender, item))
            {
                sent++;
            }
            else
            {
                notSent++;
            }
        }

        return (sent, notSent);
    }

    // One claimed attempt: load the registration, send, record the outcome. True when accepted.
    private async Task<bool> ProcessAsync(IPushNotificationSender sender, NotificationDeliveryWorkItem item)
    {
        // Signed out, permission denied, token invalid or re-owned: nothing is sent.
        var target = await _devices.GetPushTargetAsync(item.DeviceRegistrationId, item.UserId, CancellationToken.None);

        if (target is null)
        {
            await _deliveries.CompleteFailedAsync(item.DeliveryId, item.Attempt, NotificationErrorCode.DeviceInactive, _time.GetUtcNow(), CancellationToken.None);
            return false;
        }

        if (await QuietUntilAsync(item) is { } allowedAtUtc)
        {
            // Never dropped silently: deferred to the end of the quiet hours, unless the delivery
            // expires first.
            if (allowedAtUtc >= item.ExpiresAtUtc)
            {
                await _deliveries.CompleteFailedAsync(item.DeliveryId, item.Attempt, NotificationErrorCode.Expired, _time.GetUtcNow(), CancellationToken.None);
            }
            else
            {
                await _deliveries.CompleteDeferredAsync(item.DeliveryId, item.Attempt, allowedAtUtc, CancellationToken.None);
            }

            return false;
        }

        if (_relevanceChecks.TryGetValue(item.Type, out var relevance))
        {
            bool relevant;

            try
            {
                relevant = await relevance.IsStillRelevantAsync(new NotificationRelevanceContext(
                    item.UserId, item.Type, item.ResourceType, item.ResourceId, item.SourceOccurrenceKey, _time.GetUtcNow()), CancellationToken.None);
            }
            catch (Exception)
            {
                // Never sent unchecked: retried with the normal backoff inside the delivery's expiry.
                await CompleteTransientAsync(item, _time.GetUtcNow());
                return false;
            }

            if (!relevant)
            {
                await _deliveries.CompleteFailedAsync(item.DeliveryId, item.Attempt, NotificationErrorCode.NotRelevant, _time.GetUtcNow(), CancellationToken.None);
                return false;
            }
        }

        // The target/preferences/relevance reads may cross expiry after the claim checked it.
        // Re-check immediately before calling the provider: never start an expired send.
        if (_time.GetUtcNow() >= item.ExpiresAtUtc)
        {
            await _deliveries.CompleteFailedAsync(item.DeliveryId, item.Attempt, NotificationErrorCode.Expired, _time.GetUtcNow(), CancellationToken.None);
            return false;
        }

        PushSendResult result;

        try
        {
            // The attempt is pointless once its lease can be taken over.
            using var lease = new CancellationTokenSource(NotificationDeliveryPolicy.Lease, _time);
            result = await sender.SendAsync(target, NotificationCatalog.MessageFor(item), lease.Token);
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
                return true;

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
                return false;

            case PushSendResult.Rejected:
                await _deliveries.CompleteFailedAsync(item.DeliveryId, item.Attempt, NotificationErrorCode.Rejected, nowUtc, CancellationToken.None);
                return false;

            default:
                await CompleteTransientAsync(item, nowUtc);
                return false;
        }
    }

    // Retried after the policy's delay while attempts remain and the delivery has not expired; otherwise final.
    private async Task CompleteTransientAsync(NotificationDeliveryWorkItem item, DateTimeOffset nowUtc)
    {
        var (code, nextAttemptAtUtc) = NotificationDeliveryPolicy.AfterTransientFailure(item.Attempt, nowUtc, item.ExpiresAtUtc);

        if (nextAttemptAtUtc is { } next)
        {
            await _deliveries.CompleteRetryAsync(item.DeliveryId, item.Attempt, code, next, CancellationToken.None);
        }
        else
        {
            await _deliveries.CompleteFailedAsync(item.DeliveryId, item.Attempt, code, nowUtc, CancellationToken.None);
        }
    }

    // The end of the user's current quiet period when this is a reminder and now is quiet; otherwise
    // null (send now). A user without a valid zone cannot have local quiet hours evaluated: sent.
    private async Task<DateTimeOffset?> QuietUntilAsync(NotificationDeliveryWorkItem item)
    {
        if (!NotificationCatalog.IsReminder(item.Type))
        {
            return null;
        }

        var context = await _preferences.GetQuietHoursContextAsync(item.UserId, CancellationToken.None);

        if (context is null || !SetTimeZoneHandler.TryNormalizeIanaTimeZone(context.TimeZoneId, out var zoneId))
        {
            return null;
        }

        var nowUtc = _time.GetUtcNow();
        var allowedAtUtc = context.QuietHours.NextAllowedUtc(TimeZoneInfo.FindSystemTimeZoneById(zoneId), nowUtc);

        return allowedAtUtc > nowUtc ? allowedAtUtc : null;
    }
}
