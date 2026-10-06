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

    private NotificationPreferences(
        Guid userId,
        bool recurringTransactionRemindersEnabled,
        bool plannedExpenseRemindersEnabled,
        TimeOnly quietHoursStart,
        TimeOnly quietHoursEnd,
        DateTimeOffset updatedAtUtc)
    {
        UserId = userId;
        RecurringTransactionRemindersEnabled = recurringTransactionRemindersEnabled;
        PlannedExpenseRemindersEnabled = plannedExpenseRemindersEnabled;
        QuietHoursStart = quietHoursStart;
        QuietHoursEnd = quietHoursEnd;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid UserId { get; }

    public bool RecurringTransactionRemindersEnabled { get; }

    public bool PlannedExpenseRemindersEnabled { get; }

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
            QuietHours.DefaultStart,
            QuietHours.DefaultEnd,
            DateTimeOffset.MinValue);
    }

    public static NotificationPreferences Create(
        Guid userId,
        bool recurringTransactionRemindersEnabled,
        bool plannedExpenseRemindersEnabled,
        QuietHours quietHours,
        DateTimeOffset updatedAtUtc)
    {
        RequireUser(userId);
        ArgumentNullException.ThrowIfNull(quietHours);

        return new NotificationPreferences(
            userId,
            recurringTransactionRemindersEnabled,
            plannedExpenseRemindersEnabled,
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
