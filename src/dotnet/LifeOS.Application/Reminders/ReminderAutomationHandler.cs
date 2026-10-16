using LifeOS.Application.Automation;
using LifeOS.Application.Notifications;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Reminders;

namespace LifeOS.Application.Reminders;

// REM-001 §6 (R3): the user's own reminders (Custom) on the AUTO-001 engine. No scheduler of its own: the
// external tick runs discovery (FindDueAsync), the core claims each occurrence by insert on
// (user, "Reminder", occurrence key) and runs ExecuteAsync; the notification goes through the outbox,
// waits for the end of quiet hours (UserReminder is a reminder type) and is retried per device.
//
// Discovery and the execution checks are ReminderDiscovery's, shared with the conditional nudges
// (ConditionalReminderAutomationHandler), which own the other kinds.
//
// The push carries fixed copy and the reminder's id only (D-8): the title and note stay in the app.
// After every executed occurrence the reminder moves on inside the completion transaction (D-7).
public sealed class ReminderAutomationHandler(
    IReminderScheduleStore store,
    INotificationPreferencesRepository preferences,
    TimeProvider clock) : IAutomationHandler
{
    public const string Type = "Reminder";
    public const string ResourceType = "reminder";

    public const string InvalidOccurrenceKeyCode = ReminderDiscovery.InvalidOccurrenceKeyCode;
    public const string InvalidTimeZoneCode = ReminderDiscovery.InvalidTimeZoneCode;

    // D-4: a personal reminder is still worth sending up to 6 h late.
    public static readonly TimeSpan Lateness = TimeSpan.FromHours(6);

    private static readonly IReadOnlyList<ReminderKind> Kinds = [ReminderKind.Custom];

    public string AutomationType => Type;

    public TimeSpan MaxLateness => Lateness;

    public Task<IReadOnlyList<DueOccurrence>> FindDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken) =>
        ReminderDiscovery.FindDueAsync(store, Type, Lateness, Kinds, nowUtc, limit, cancellationToken);

    public async Task<AutomationResult> ExecuteAsync(AutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        var (done, reminder, of, _) = await ReminderDiscovery.StartAsync(store, occurrence, kind => kind == ReminderKind.Custom, cancellationToken);

        if (done is not null)
        {
            return done;
        }

        // The "Personal reminders" switch was turned off after discovery: nothing is sent, the reminder
        // still moves on.
        if (!(await preferences.GetAsync(occurrence.UserId, cancellationToken)).IsEnabled(NotificationType.UserReminder))
        {
            return AutomationResult.Succeeded(saveArtifact: ReminderDiscovery.Advance(store, clock, occurrence.UserId, of!, ReminderOutcome.Disabled));
        }

        return AutomationResult.Succeeded(
            notification: new AutomationNotification(NotificationType.UserReminder, ResourceType, reminder!.Id),
            saveArtifact: ReminderDiscovery.Advance(store, clock, occurrence.UserId, of!, ReminderOutcome.Notified));
    }
}
