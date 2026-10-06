namespace LifeOS.Contracts.Notifications;

// AUTO-003A: GET/PUT /api/notification-preferences. Quiet hours are local times in the user's time
// zone, "HH:mm" (24-hour).
public sealed record NotificationPreferencesResponse(
    bool RecurringTransactionReminders,
    bool PlannedExpenseReminders,
    string QuietHoursStart,
    string QuietHoursEnd);

// Every field is required (the whole form is saved at once).
public sealed record SetNotificationPreferencesRequest(
    bool? RecurringTransactionReminders,
    bool? PlannedExpenseReminders,
    string? QuietHoursStart,
    string? QuietHoursEnd);
