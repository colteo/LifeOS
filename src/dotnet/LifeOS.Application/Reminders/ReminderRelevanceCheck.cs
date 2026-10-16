using LifeOS.Application.Notifications;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Reminders;

namespace LifeOS.Application.Reminders;

// REM-001 §6.6: the Reminders implementation of the notification core's relevance check, for the habit
// and water nudges. Right before each send attempt (after quiet hours, on every retry) it re-reads the
// reminder named by the delivery's source occurrence key and asks the same ReminderConditionEvaluator as
// the execution, for the occurrence's own date. Not relevant: an invalid key, a reminder deleted, paused
// or no longer conditional, or a condition already met (habit done, excused, not scheduled, archived,
// week goal met; water target reached or removed). Read-only: the reminder's outcome stays Notified.
public sealed class ReminderRelevanceCheck(
    IReminderScheduleStore store,
    INotificationPreferencesRepository preferences,
    ReminderConditionEvaluator evaluator) : INotificationRelevanceCheck
{
    private static readonly IReadOnlySet<NotificationType> OwnedTypes =
        new HashSet<NotificationType> { NotificationType.HabitReminder, NotificationType.HydrationReminder };

    public IReadOnlySet<NotificationType> Types => OwnedTypes;

    public async Task<bool> IsStillRelevantAsync(NotificationRelevanceContext context, CancellationToken cancellationToken)
    {
        if (!ReminderOccurrenceKey.TryParse(context.SourceOccurrenceKey, out var of))
        {
            return false;
        }

        var reminder = await store.FindAsync(context.UserId, of.ReminderId, cancellationToken);

        if (reminder is null || reminder.Status == ReminderStatus.Paused || !reminder.IsConditional)
        {
            return false;
        }

        // A snooze is judged on its date in the user's current zone (UTC while unknown).
        var zone = TimeZoneInfo.Utc;

        if (of.SnoozeUtc is not null)
        {
            var quietHours = await preferences.GetQuietHoursContextAsync(context.UserId, cancellationToken);
            ReminderDiscovery.TryFindZone(quietHours?.TimeZoneId, out zone);
        }

        return await evaluator.IsNeededAsync(reminder, ReminderConditionEvaluator.OccurrenceDate(of, zone), cancellationToken);
    }
}
