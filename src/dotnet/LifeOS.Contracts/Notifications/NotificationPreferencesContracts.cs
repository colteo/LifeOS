namespace LifeOS.Contracts.Notifications;

// AUTO-003A: GET/PUT /api/notification-preferences. Quiet hours are local times in the user's time
// zone, "HH:mm" (24-hour). UserReminders (REM-001) is the "Personal reminders" switch; it is last and
// optional so clients released before it keep working.
public sealed record NotificationPreferencesResponse(
    bool RecurringTransactionReminders,
    bool PlannedExpenseReminders,
    string QuietHoursStart,
    string QuietHoursEnd,
    bool UserReminders = true);

// Every field is required (the whole form is saved at once), except UserReminders: null keeps the
// saved value.
public sealed record SetNotificationPreferencesRequest(
    bool? RecurringTransactionReminders,
    bool? PlannedExpenseReminders,
    string? QuietHoursStart,
    string? QuietHoursEnd,
    bool? UserReminders = null);
