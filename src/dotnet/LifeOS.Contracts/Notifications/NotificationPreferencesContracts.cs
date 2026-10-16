namespace LifeOS.Contracts.Notifications;

// AUTO-003A: GET/PUT /api/notification-preferences. Quiet hours are local times in the user's time
// zone, "HH:mm" (24-hour). UserReminders (REM-001) is the "Personal reminders" switch; HabitReminders and
// HydrationReminders (REM-001 R5) are "Habit reminders" and "Water reminders". They are last and optional
// so clients released before them (Android 1.7.0) keep working.
public sealed record NotificationPreferencesResponse(
    bool RecurringTransactionReminders,
    bool PlannedExpenseReminders,
    string QuietHoursStart,
    string QuietHoursEnd,
    bool UserReminders = true,
    bool HabitReminders = true,
    bool HydrationReminders = true);

// Every field is required (the whole form is saved at once), except the REM-001 switches: null keeps the
// saved value.
public sealed record SetNotificationPreferencesRequest(
    bool? RecurringTransactionReminders,
    bool? PlannedExpenseReminders,
    string? QuietHoursStart,
    string? QuietHoursEnd,
    bool? UserReminders = null,
    bool? HabitReminders = null,
    bool? HydrationReminders = null);
