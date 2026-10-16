using LifeOS.Application.Habits;
using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Domain.Habits;
using LifeOS.Domain.Reminders;

namespace LifeOS.Application.Reminders;

// REM-001 D-6 / §6.3: the single rule of the conditional reminders, used both when an occurrence runs
// (ConditionalReminderAutomationHandler) and right before each send attempt (ReminderRelevanceCheck).
// Read-only: Habits and Hydration are read only through their ports, nothing is stored or cached.
//
// HabitIncomplete: needed while the habit is active and still open on the date (Pending: scheduled and
// neither Done nor Excused) and, for a times-per-week habit, its week goal is not met yet.
// HydrationGoal: needed while the date has a water target that is not reached. Custom: always.
public sealed class ReminderConditionEvaluator(IHabitDayStatusReader habits, IHydrationDayReader hydration)
{
    public async Task<bool> IsNeededAsync(Reminder reminder, DateOnly date, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reminder);

        switch (reminder.Kind)
        {
            case ReminderKind.HabitIncomplete:
                var habit = await habits.GetStatusAsync(reminder.UserId, reminder.HabitId!.Value, date, cancellationToken);

                return habit is { IsActive: true, Status: HabitDayStatus.Pending }
                    && (habit.WeekTarget is not { } target || habit.WeekDone < target);

            case ReminderKind.HydrationGoal:
                var day = (await hydration.GetDaysAsync(reminder.UserId, date, date, cancellationToken)).Single();

                return day.TargetMl is not null && !day.IsTargetMet;

            default:
                return true;
        }
    }

    // The local date an occurrence is about: a scheduled occurrence's own date (a 23:50 nudge executed or
    // retried after midnight still evaluates its day); a snooze's date in the zone.
    public static DateOnly OccurrenceDate(ReminderOccurrence occurrence, TimeZoneInfo zone) =>
        occurrence.Local is { } local
            ? DateOnly.FromDateTime(local)
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(occurrence.SnoozeUtc!.Value, zone).DateTime);
}
