using LifeOS.Domain.Notifications;

namespace LifeOS.Application.Notifications;

// AUTO-001 §11: sends one message to one device. Provider-neutral: the FCM HTTP v1 implementation
// (WP3B) lives in Infrastructure and maps provider responses to these outcomes. Until a sender is
// registered, notification dispatch (tick Phase A) is disabled.
public interface IPushNotificationSender
{
    Task<PushSendResult> SendAsync(PushTarget target, PushMessage message, CancellationToken cancellationToken);
}

public enum PushSendResult
{
    // The provider took the message: the delivery is Sent.
    Accepted,

    // Worth retrying later (network, timeout, provider unavailable or throttling).
    Transient,

    // The token is unregistered or invalid: the delivery fails and the registration becomes Inactive.
    TokenInvalid,

    // Any other refusal: the delivery fails without retry.
    Rejected
}

// The token is sensitive: ToString never includes it, so a logged target cannot leak it.
public sealed record PushTarget(PushProvider Provider, string Token)
{
    public override string ToString() => $"PushTarget {{ Provider = {Provider} }}";
}

// Fixed copy and opaque data only (PD-3, §11). Tag = notification key, so a resend after a crash
// replaces the first notification on the device instead of duplicating it.
public sealed record PushMessage(string Title, string Body, IReadOnlyDictionary<string, string> Data, string Tag);
