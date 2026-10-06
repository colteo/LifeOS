using LifeOS.Domain.Notifications;

namespace LifeOS.Application.Notifications;

// The user's current IANA zone (null when the device never reported one) and quiet hours: what the
// dispatcher needs to hold a reminder back.
public sealed record QuietHoursContext(string? TimeZoneId, QuietHours QuietHours);

// AUTO-003A: notification_preferences, at most one row per user. Absence of a row = the defaults.
// Every read is scoped to one user id.
public interface INotificationPreferencesRepository
{
    // The user's preferences, or NotificationPreferences.Default when none are saved.
    Task<NotificationPreferences> GetAsync(Guid userId, CancellationToken cancellationToken);

    // Creates or replaces the user's preferences. False when the user does not exist.
    Task<bool> SetAsync(NotificationPreferences preferences, CancellationToken cancellationToken);

    // Null when the user does not exist.
    Task<QuietHoursContext?> GetQuietHoursContextAsync(Guid userId, CancellationToken cancellationToken);
}

// What the settings screen shows (the defaults when nothing is saved).
public sealed record NotificationPreferencesView(
    bool RecurringTransactionReminders,
    bool PlannedExpenseReminders,
    TimeOnly QuietHoursStart,
    TimeOnly QuietHoursEnd);

public sealed class GetNotificationPreferencesHandler(INotificationPreferencesRepository preferences)
{
    public async Task<NotificationPreferencesView> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var saved = await preferences.GetAsync(userId, cancellationToken);

        return new NotificationPreferencesView(
            saved.RecurringTransactionRemindersEnabled,
            saved.PlannedExpenseRemindersEnabled,
            saved.QuietHoursStart,
            saved.QuietHoursEnd);
    }
}

public enum SetNotificationPreferencesResult
{
    Saved,
    InvalidQuietHours,
    NotFound
}

// Replaces all preferences at once (the settings screen sends the whole form). Disabling a reminder
// stops future reminders; one already enqueued is still delivered.
public sealed class SetNotificationPreferencesHandler(INotificationPreferencesRepository preferences, TimeProvider clock)
{
    public async Task<SetNotificationPreferencesResult> HandleAsync(
        Guid userId,
        bool recurringTransactionReminders,
        bool plannedExpenseReminders,
        TimeOnly quietHoursStart,
        TimeOnly quietHoursEnd,
        CancellationToken cancellationToken)
    {
        if (!QuietHours.IsValid(quietHoursStart, quietHoursEnd))
        {
            return SetNotificationPreferencesResult.InvalidQuietHours;
        }

        var saved = NotificationPreferences.Create(
            userId,
            recurringTransactionReminders,
            plannedExpenseReminders,
            QuietHours.Create(quietHoursStart, quietHoursEnd),
            clock.GetUtcNow());

        return await preferences.SetAsync(saved, cancellationToken)
            ? SetNotificationPreferencesResult.Saved
            : SetNotificationPreferencesResult.NotFound;
    }
}
