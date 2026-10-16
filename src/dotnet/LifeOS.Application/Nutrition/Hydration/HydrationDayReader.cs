using LifeOS.Domain.Nutrition.Hydration;

namespace LifeOS.Application.Nutrition.Hydration;

// The single implementation of the day rule (HYD-001 §7.2): totals from the intakes, the target each date
// had from the user's target versions, met = total >= that target.
public sealed class HydrationDayReader(IHydrationRepository repository) : IHydrationDayReader
{
    public async Task<IReadOnlyList<HydrationDay>> GetDaysAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (to < from)
        {
            return [];
        }

        var totals = await repository.GetDailyTotalsAsync(userId, from, to, cancellationToken);
        var schedule = new HydrationTargetSchedule(await repository.GetTargetVersionsAsync(userId, to, cancellationToken));
        var days = new List<HydrationDay>(to.DayNumber - from.DayNumber + 1);

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            days.Add(schedule.Day(date, totals.GetValueOrDefault(date)));

            if (date == DateOnly.MaxValue)
            {
                break;
            }
        }

        return days;
    }

    public async Task<HydrationDay> GetDayAsync(Guid userId, DateOnly date, CancellationToken cancellationToken) =>
        (await GetDaysAsync(userId, date, date, cancellationToken)).Single();
}
