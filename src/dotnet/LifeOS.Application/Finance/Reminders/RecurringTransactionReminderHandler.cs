using System.Globalization;
using LifeOS.Application.Automation;
using LifeOS.Application.Finance.Recurring;
using LifeOS.Application.Notifications;
using LifeOS.Domain.Finance.Recurring;
using LifeOS.Domain.Notifications;

namespace LifeOS.Application.Finance.Reminders;

// AUTO-003A reminder A: a recurring Income/Expense occurrence that needs manual confirmation (FIN-004,
// ADR-009). One logical reminder per occurrence: the key is the occurrence identity RuleId + year +
// month ("<rule id>:2026-10"), never the scheduled date, so editing the rule cannot remind the same
// month twice. Reminded at 09:00 local on the scheduled date (FinanceReminderSchedule).
//
// Read-only: the occurrence is derived with the Domain rules (Due = unprocessed and scheduled on/before
// local today) from the existing IRecurringRepository read; nothing is confirmed, skipped or created.
public sealed class RecurringTransactionReminderHandler(
    IFinanceReminderRepository reminders,
    IRecurringRepository recurring,
    INotificationPreferencesRepository preferences,
    TimeProvider clock) : IAutomationHandler
{
    public const string Type = "FinanceRecurringReminder";
    public const string ResourceType = "finance_recurring";

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

        var due = await reminders.FindDueRecurringAsync(Type, open.Values.Select(entry => entry.Zone).ToList(), limit, cancellationToken);

        return due
            .Where(item => open.TryGetValue(item.TimeZoneId, out var zone) && zone.Zone.LocalDate == item.LocalDate)
            .Select(item => new DueOccurrence(
                item.UserId,
                OccurrenceKey(item.RuleId, item.LocalDate.Year, item.LocalDate.Month),
                item.TimeZoneId,
                open[item.TimeZoneId].DueAtUtc))
            .ToList();
    }

    public async Task<AutomationResult> ExecuteAsync(AutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        if (!TryParseOccurrenceKey(occurrence.OccurrenceKey, out var ruleId, out var year, out var month))
        {
            return AutomationResult.PermanentFailure(FinanceReminderSchedule.InvalidOccurrenceKeyCode);
        }

        if (!FinanceReminderSchedule.TryFindZone(occurrence.TimeZoneId, out var zone))
        {
            return AutomationResult.PermanentFailure(FinanceReminderSchedule.InvalidTimeZoneCode);
        }

        if (!(await preferences.GetAsync(occurrence.UserId, cancellationToken)).IsEnabled(NotificationType.RecurringTransactionReminder))
        {
            return AutomationResult.NotApplicable();
        }

        // Re-derived now: confirmed, skipped, deleted, out-of-range or no longer due since discovery
        // means nothing to remind.
        var read = await recurring.ReadAsync(occurrence.UserId, year, month, year, month, cancellationToken);
        var rule = read.Rules.SingleOrDefault(candidate => candidate.Id == ruleId);

        if (rule is null || !rule.IncludesMonth(year, month))
        {
            return AutomationResult.NotApplicable();
        }

        var state = read.States.SingleOrDefault(candidate => candidate.RecurringRuleId == ruleId && candidate.Year == year && candidate.Month == month);

        if (rule.Status(year, month, FinanceReminderSchedule.LocalToday(zone, clock.GetUtcNow()), state) != OccurrenceStatus.Due)
        {
            return AutomationResult.NotApplicable();
        }

        return AutomationResult.Succeeded(
            notification: new AutomationNotification(NotificationType.RecurringTransactionReminder, ResourceType, ruleId));
    }

    public static string OccurrenceKey(Guid ruleId, int year, int month) =>
        string.Create(CultureInfo.InvariantCulture, $"{ruleId:D}:{year:D4}-{month:D2}");

    public static bool TryParseOccurrenceKey(string? key, out Guid ruleId, out int year, out int month)
    {
        ruleId = Guid.Empty;
        year = 0;
        month = 0;

        if (key is not { Length: 44 } || key[36] != ':'
            || !Guid.TryParseExact(key[..36], "D", out ruleId) || ruleId == Guid.Empty
            || !DateOnly.TryParseExact(key[37..] + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var firstDay)
            || firstDay.Year > 9998)
        {
            return false;
        }

        year = firstDay.Year;
        month = firstDay.Month;
        return true;
    }
}
