using System.Globalization;
using LifeOS.Application.Automation;
using LifeOS.Application.Finance.PlannedExpenses;
using LifeOS.Application.Notifications;
using LifeOS.Domain.Finance.PlannedExpenses;
using LifeOS.Domain.Notifications;

namespace LifeOS.Application.Finance.Reminders;

// AUTO-003A reminder B: a one-off planned expense that becomes due (FIN-005, ADR-010). One logical
// reminder per expense and scheduled date ("<expense id>:2026-10-06"): rescheduling an unresolved
// expense to another date reminds again on that date, never twice for one date. Reminded at 09:00 local
// on the scheduled date (FinanceReminderSchedule).
//
// Read-only: Due is the Domain status (unresolved and scheduled on/before local today), read through
// the existing IPlannedExpenseRepository; nothing is confirmed, cancelled or created.
public sealed class PlannedExpenseReminderHandler(
    IFinanceReminderRepository reminders,
    IPlannedExpenseRepository plannedExpenses,
    INotificationPreferencesRepository preferences,
    TimeProvider clock) : IAutomationHandler
{
    public const string Type = "FinancePlannedExpenseReminder";
    public const string ResourceType = "finance_planned_expense";

    public string AutomationType => Type;

    public TimeSpan MaxLateness => FinanceReminderSchedule.Lateness;

    public async Task<IReadOnlyList<DueOccurrence>> FindDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return [];
        }

        var open = FinanceReminderSchedule.OpenZones(await reminders.GetUserTimeZoneIdsAsync(cancellationToken), nowUtc);

        if (open.Count == 0)
        {
            return [];
        }

        var due = await reminders.FindDuePlannedExpensesAsync(Type, open.Values.Select(entry => entry.Zone).ToList(), limit, cancellationToken);

        return due
            .Where(item => open.TryGetValue(item.TimeZoneId, out var zone) && zone.Zone.LocalDate == item.LocalDate)
            .Select(item => new DueOccurrence(
                item.UserId,
                OccurrenceKey(item.PlannedExpenseId, item.LocalDate),
                item.TimeZoneId,
                open[item.TimeZoneId].DueAtUtc))
            .ToList();
    }

    public async Task<AutomationResult> ExecuteAsync(AutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        if (!TryParseOccurrenceKey(occurrence.OccurrenceKey, out var plannedExpenseId, out var scheduledDate))
        {
            return AutomationResult.PermanentFailure(FinanceReminderSchedule.InvalidOccurrenceKeyCode);
        }

        if (!FinanceReminderSchedule.TryFindZone(occurrence.TimeZoneId, out var zone))
        {
            return AutomationResult.PermanentFailure(FinanceReminderSchedule.InvalidTimeZoneCode);
        }

        if (!(await preferences.GetAsync(occurrence.UserId, cancellationToken)).IsEnabled(NotificationType.PlannedExpenseReminder))
        {
            return AutomationResult.NotApplicable();
        }

        // Re-read now: confirmed, cancelled, deleted or rescheduled since discovery means nothing to
        // remind for this date.
        var snapshot = await plannedExpenses.GetAsync(occurrence.UserId, plannedExpenseId, cancellationToken);

        if (snapshot.Item is not { } item
            || item.ScheduledDate != scheduledDate
            || item.Status(FinanceReminderSchedule.LocalToday(zone, clock.GetUtcNow()), snapshot.State) != PlannedExpenseStatus.Due)
        {
            return AutomationResult.NotApplicable();
        }

        return AutomationResult.Succeeded(
            notification: new AutomationNotification(NotificationType.PlannedExpenseReminder, ResourceType, plannedExpenseId));
    }

    public static string OccurrenceKey(Guid plannedExpenseId, DateOnly scheduledDate) =>
        string.Create(CultureInfo.InvariantCulture, $"{plannedExpenseId:D}:{scheduledDate:yyyy-MM-dd}");

    public static bool TryParseOccurrenceKey(string? key, out Guid plannedExpenseId, out DateOnly scheduledDate)
    {
        plannedExpenseId = Guid.Empty;
        scheduledDate = default;

        return key is { Length: 47 } && key[36] == ':'
            && Guid.TryParseExact(key[..36], "D", out plannedExpenseId) && plannedExpenseId != Guid.Empty
            && DateOnly.TryParseExact(key[37..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out scheduledDate);
    }
}
