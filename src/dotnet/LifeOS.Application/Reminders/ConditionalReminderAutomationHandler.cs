using LifeOS.Application.Automation;
using LifeOS.Application.Notifications;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Reminders;

namespace LifeOS.Application.Reminders;

// REM-001 §6 (R5): the conditional nudges (HabitIncomplete, HydrationGoal) on the same engine, discovery
// and outbox as the user's own reminders, with their own automation type and a 2 h lateness (D-4): a
// nudge about "today" goes stale fast.
//
// When an occurrence runs, ReminderConditionEvaluator decides for the occurrence's local date: still
// needed → one HabitReminder / HydrationReminder notification (fixed copy; the habit's id or nothing),
// otherwise Succeeded without a notification and the reminder records Suppressed (D-6). Either way the
// reminder moves on inside the completion transaction (D-7). Before each send attempt the same
// evaluator runs again (ReminderRelevanceCheck, §6.6).
public sealed class ConditionalReminderAutomationHandler(
    IReminderScheduleStore store,
    INotificationPreferencesRepository preferences,
    ReminderConditionEvaluator evaluator,
    TimeProvider clock) : IAutomationHandler
{
    public const string Type = "ConditionalReminder";
    public const string HabitResourceType = "habit";
    public const string HydrationResourceType = "hydration";

    // D-4: a nudge is worth sending up to 2 h late.
    public static readonly TimeSpan Lateness = TimeSpan.FromHours(2);

    private static readonly IReadOnlyList<ReminderKind> Kinds = [ReminderKind.HabitIncomplete, ReminderKind.HydrationGoal];

    public string AutomationType => Type;

    public TimeSpan MaxLateness => Lateness;

    public static NotificationType NotificationTypeOf(ReminderKind kind) => kind switch
    {
        ReminderKind.HabitIncomplete => NotificationType.HabitReminder,
        ReminderKind.HydrationGoal => NotificationType.HydrationReminder,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public Task<IReadOnlyList<DueOccurrence>> FindDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken) =>
        ReminderDiscovery.FindDueAsync(store, Type, Lateness, Kinds, nowUtc, limit, cancellationToken);

    public async Task<AutomationResult> ExecuteAsync(AutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        var (done, reminder, of, zone) = await ReminderDiscovery.StartAsync(store, occurrence, Kinds.Contains, cancellationToken);

        if (done is not null)
        {
            return done;
        }

        var type = NotificationTypeOf(reminder!.Kind);

        // The "Habit reminders" / "Water reminders" switch was turned off after discovery.
        if (!(await preferences.GetAsync(occurrence.UserId, cancellationToken)).IsEnabled(type))
        {
            return AutomationResult.Succeeded(saveArtifact: ReminderDiscovery.Advance(store, clock, occurrence.UserId, of!, ReminderOutcome.Disabled));
        }

        if (!await evaluator.IsNeededAsync(reminder, ReminderConditionEvaluator.OccurrenceDate(of!, zone), cancellationToken))
        {
            return AutomationResult.Succeeded(saveArtifact: ReminderDiscovery.Advance(store, clock, occurrence.UserId, of!, ReminderOutcome.Suppressed));
        }

        var notification = reminder.Kind == ReminderKind.HabitIncomplete
            ? new AutomationNotification(type, HabitResourceType, reminder.HabitId)
            : new AutomationNotification(type, HydrationResourceType);

        return AutomationResult.Succeeded(
            notification: notification,
            saveArtifact: ReminderDiscovery.Advance(store, clock, occurrence.UserId, of!, ReminderOutcome.Notified));
    }
}
