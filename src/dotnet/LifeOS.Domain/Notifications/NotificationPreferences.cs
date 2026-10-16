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
    public const bool DefaultHabitRemindersEnabled = true;
    public const bool DefaultHydrationRemindersEnabled = true;

    private NotificationPreferences(
        Guid userId,
        bool recurringTransactionRemindersEnabled,
        bool plannedExpenseRemindersEnabled,
        bool userRemindersEnabled,
        bool habitRemindersEnabled,
        bool hydrationRemindersEnabled,
        TimeOnly quietHoursStart,
        TimeOnly quietHoursEnd,
        DateTimeOffset updatedAtUtc)
    {
        UserId = userId;
        RecurringTransactionRemindersEnabled = recurringTransactionRemindersEnabled;
        PlannedExpenseRemindersEnabled = plannedExpenseRemindersEnabled;
        UserRemindersEnabled = userRemindersEnabled;
        HabitRemindersEnabled = habitRemindersEnabled;
        HydrationRemindersEnabled = hydrationRemindersEnabled;
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

    // REM-001 D-12 (R5): the global switches of the conditional nudges ("Habit reminders", "Water
    // reminders"). Each nudge is created on purpose; turning one off mutes all of that type and keeps them.
    public bool HabitRemindersEnabled { get; }

    public bool HydrationRemindersEnabled { get; }

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
            DefaultHabitRemindersEnabled,
            DefaultHydrationRemindersEnabled,
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
        DateTimeOffset updatedAtUtc,
        bool habitRemindersEnabled = DefaultHabitRemindersEnabled,
        bool hydrationRemindersEnabled = DefaultHydrationRemindersEnabled)
    {
        RequireUser(userId);
        ArgumentNullException.ThrowIfNull(quietHours);

        return new NotificationPreferences(
            userId,
            recurringTransactionRemindersEnabled,
            plannedExpenseRemindersEnabled,
            userRemindersEnabled,
            habitRemindersEnabled,
            hydrationRemindersEnabled,
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
        NotificationType.HabitReminder => HabitRemindersEnabled,
        NotificationType.HydrationReminder => HydrationRemindersEnabled,
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
