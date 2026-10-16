using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Domain.Habits;

namespace LifeOS.Application.Habits;

// The single implementation of IHabitDayStatusReader: the habit's stored schedule and check-ins of the
// date's ISO week (enough for the day and the week goal) and, for a water habit, Hydration's days of
// that week through IHydrationDayReader, evaluated by HabitProgressCalculator with the date as "today".
public sealed class HabitDayStatusReader(IHabitRepository repository, IHydrationDayReader hydration) : IHabitDayStatusReader
{
    public async Task<HabitDayStatusSnapshot?> GetStatusAsync(Guid userId, Guid habitId, DateOnly date, CancellationToken cancellationToken)
    {
        if (await repository.GetAsync(userId, habitId, cancellationToken) is not { } habit)
        {
            return null;
        }

        var weekStart = HabitProgressCalculator.IsoWeekStart(date);
        var checkIns = await repository.GetCheckInsAsync(userId, [habit.Id], weekStart, date, cancellationToken);
        var water = habit.CompletionSource == HabitCompletionSource.Hydration
            ? HabitWater.TargetMet(await HabitWater.DaysAsync(hydration, userId, weekStart, date, cancellationToken))
            : null;
        var calculator = new HabitProgressCalculator(habit, checkIns, date, weekStart, water);
        var week = habit.VersionOn(date)?.Kind == HabitFrequencyKind.TimesPerWeek ? calculator.Week(date) : null;

        return new HabitDayStatusSnapshot(habit.Id, habit.Status == HabitStatus.Active, calculator.StatusOn(date), week?.Done, week?.Target);
    }
}
