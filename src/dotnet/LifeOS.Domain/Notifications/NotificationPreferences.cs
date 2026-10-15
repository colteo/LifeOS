namespace LifeOS.Domain.Notifications;

// AUTO-003A: a user's reminder notification preferences. At most one row per user; no row means the
// defaults (every reminder type enabled, quiet hours 22:00–08:00), so no backfill is ever needed.
//
// One flag per reminder notification type. Adding a reminder type adds one flag here. Weekly Review
// keeps its own module-owned setting (AUTO-002) and is not a reminder: quiet hours do not apply to it.
public sealed class NotificationPreferences
{
    public const bool DefaultRecurringTransactionRemindersEnabled = true;
    public const bool DefaultPlannedExpenseRemindersEnabled = true;
    public const bool DefaultUserRemindersEnabled = true;

    private NotificationPreferences(
        Guid userId,
        bool recurringTransactionRemindersEnabled,
        bool plannedExpenseRemindersEnabled,
        bool userRemindersEnabled,
        TimeOnly quietHoursStart,
        TimeOnly quietHoursEnd,
        DateTimeOffset updatedAtUtc)
    {
        UserId = userId;
        RecurringTransactionRemindersEnabled = recurringTransactionRemindersEnabled;
        PlannedExpenseRemindersEnabled = plannedExpenseRemindersEnabled;
        UserRemindersEnabled = userRemindersEnabled;
        QuietHoursStart = quietHoursStart;
        QuietHoursEnd = quietHoursEnd;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid UserId { get; }

    public bool RecurringTransactionRemindersEnabled { get; }

    public bool PlannedExpenseRemindersEnabled { get; }

    // REM-001 D-12: the global switch for the user's own reminders ("Personal reminders"). Each reminder
    // is already created on purpose; turning this off mutes all of them and keeps them.
    public bool UserRemindersEnabled { get; }

    public TimeOnly QuietHoursStart { get; }

    public TimeOnly QuietHoursEnd { get; }

    // DateTimeOffset.MinValue for the defaults, which are never saved.
    public DateTimeOffset UpdatedAtUtc { get; }

    public QuietHours QuietHours => QuietHours.Create(QuietHoursStart, QuietHoursEnd);

    public static NotificationPreferences Default(Guid userId)
    {
        RequireUser(userId);

        return new NotificationPreferences(
            userId,
            DefaultRecurringTransactionRemindersEnabled,
            DefaultPlannedExpenseRemindersEnabled,
            DefaultUserRemindersEnabled,
            QuietHours.DefaultStart,
            QuietHours.DefaultEnd,
            DateTimeOffset.MinValue);
    }

    public static NotificationPreferences Create(
        Guid userId,
        bool recurringTransactionRemindersEnabled,
        bool plannedExpenseRemindersEnabled,
        bool userRemindersEnabled,
        QuietHours quietHours,
        DateTimeOffset updatedAtUtc)
    {
        RequireUser(userId);
        ArgumentNullException.ThrowIfNull(quietHours);

        return new NotificationPreferences(
            userId,
            recurringTransactionRemindersEnabled,
            plannedExpenseRemindersEnabled,
            userRemindersEnabled,
            quietHours.Start,
            quietHours.End,
            updatedAtUtc.ToUniversalTime());
    }

    // Whether the user receives this notification type. Types that are not reminders are always on here
    // (Test is diagnostics; Weekly Review has its own setting).
    public bool IsEnabled(NotificationType type) => type switch
    {
        NotificationType.RecurringTransactionReminder => RecurringTransactionRemindersEnabled,
        NotificationType.PlannedExpenseReminder => PlannedExpenseRemindersEnabled,
        NotificationType.UserReminder => UserRemindersEnabled,
        _ => true
    };

    private static void RequireUser(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }
    }
}
