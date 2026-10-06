using LifeOS.Domain.Notifications;

namespace LifeOS.Application.Notifications;

// One logical notification to a user, fanned out to one delivery per Active device (PD-7).
// The key is deterministic, so enqueueing the same notification twice creates nothing new.
public sealed record LogicalNotification(
    Guid UserId,
    string NotificationKey,
    NotificationType Type,
    Guid? SourceExecutionId,
    string? ResourceType,
    Guid? ResourceId,
    DateTimeOffset ExpiresAtUtc)
{
    public const string AutomationKeyPrefix = "automation:";
    public const string TestKeyPrefix = "test:";

    // The notification of a succeeded automation execution: key "automation:<execution id>".
    public static LogicalNotification ForAutomation(
        Guid executionId,
        Guid userId,
        NotificationType type,
        string? resourceType,
        Guid? resourceId,
        DateTimeOffset nowUtc) =>
        new(userId, AutomationKeyPrefix + executionId.ToString("D"), type, executionId, resourceType, resourceId, nowUtc + NotificationCatalog.ExpiryOf(type));

    // A diagnostics test notification: key "test:<uuid v7>".
    public static LogicalNotification Test(Guid userId, DateTimeOffset nowUtc) =>
        new(userId, TestKeyPrefix + Guid.CreateVersion7().ToString("D"), NotificationType.Test, null, null, null, nowUtc + NotificationCatalog.ExpiryOf(NotificationType.Test));
}

// AUTO-001 §11 (PD-3): fixed English copy per type; no personal data, no amounts, no free text.
// The data payload carries only a type and an opaque id.
public static class NotificationCatalog
{
    public const string Title = "LifeOS";

    // A reminder's 24 h exceeds any quiet period (always < 24 h), so a reminder deferred by quiet hours
    // is still sent when they end (AUTO-003A).
    public static TimeSpan ExpiryOf(NotificationType type) => type switch
    {
        NotificationType.Test => TimeSpan.FromMinutes(15),
        NotificationType.WeeklyReviewReady => TimeSpan.FromHours(24),
        NotificationType.RecurringTransactionReminder => TimeSpan.FromHours(24),
        NotificationType.PlannedExpenseReminder => TimeSpan.FromHours(24),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    // AUTO-003A: quiet hours apply to reminders only. The weekly review (Sunday 20:00) and the
    // diagnostics test notification are never held back.
    public static bool IsReminder(NotificationType type) =>
        type is NotificationType.RecurringTransactionReminder or NotificationType.PlannedExpenseReminder;

    // Reminder copy never contains an amount, balance, category, payee, account or email.
    public static PushMessage MessageFor(NotificationDeliveryWorkItem item)
    {
        var (body, dataType) = item.Type switch
        {
            NotificationType.Test => ("Test notification from LifeOS", "test"),
            NotificationType.WeeklyReviewReady => ("Your weekly review is ready", "weekly_review"),
            NotificationType.RecurringTransactionReminder => ("A recurring transaction needs your confirmation", "finance_recurring"),
            NotificationType.PlannedExpenseReminder => ("A planned expense is due", "finance_planned_expense"),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.Type, null)
        };

        var data = new Dictionary<string, string> { ["type"] = item.ResourceType ?? dataType };

        if (item.ResourceId is { } resourceId)
        {
            data["id"] = resourceId.ToString("D");
        }

        return new PushMessage(Title, body, data, item.NotificationKey);
    }
}
