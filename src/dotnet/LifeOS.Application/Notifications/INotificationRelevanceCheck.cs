using LifeOS.Domain.Notifications;

namespace LifeOS.Application.Notifications;

// REM-001 §6.6: an extension point of the notification core, consulted by NotificationDispatcher right
// before each send attempt of a delivery whose type the check owns (after quiet hours, so a deferred or
// retried delivery is judged again when it is finally sent). Implemented by a module (Reminders); the
// core references no module: modules plug in through this port, like IAutomationHandler.
public interface INotificationRelevanceCheck
{
    // The notification types this check owns. At most one check per type.
    IReadOnlySet<NotificationType> Types { get; }

    // Read-only. False: the delivery is not sent (Failed, NotRelevant). An exception is a transient
    // failure: the attempt is retried with the normal backoff and is never sent unchecked.
    Task<bool> IsStillRelevantAsync(NotificationRelevanceContext context, CancellationToken cancellationToken);
}

// SourceOccurrenceKey: the occurrence key of the automation execution that produced the notification
// (null for notifications without one, e.g. the test notification).
public sealed record NotificationRelevanceContext(
    Guid UserId,
    NotificationType Type,
    string? ResourceType,
    Guid? ResourceId,
    string? SourceOccurrenceKey,
    DateTimeOffset NowUtc);
