using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Domain.Habits;
using LifeOS.Domain.Nutrition.Hydration;

namespace LifeOS.Application.Habits;

// HAB-001 §7 read use cases. Statistics are computed on every read by HabitProgressCalculator from the
// stored schedule and check-ins (and, for the water habit, Hydration's days read through
// IHydrationDayReader); nothing derived is stored.

// The Today list for one local date (today or an earlier day): each active habit's status on that date,
// its group, current streak and that date's week goal.
public sealed class GetHabitsForDayHandler(IHabitRepository repository, IHydrationDayReader hydration, TimeProvider clock)
{
    // Enough check-ins for most current streaks in one query; a streak that reaches this limit is
    // completed with one more, still bounded, query for that habit.
    public const int LoadedHistoryDays = 60;

    public async Task<HabitResult<HabitsForDay>> HandleAsync(Guid userId, DateOnly date, int utcOffsetMinutes, CancellationToken cancellationToken)
    {
        DateOnly today;

        try
        {
            today = HabitLimits.LocalToday(clock, utcOffsetMinutes);

            if (date > today)
            {
                throw new ArgumentOutOfRangeException(nameof(date), "You can't view a future date.");
            }
        }
        catch (ArgumentException exception)
        {
            return HabitResult<HabitsForDay>.Invalid(exception);
        }

        var habits = await repository.GetAllAsync(userId, cancellationToken);
        var active = habits.Where(habit => habit.Status == HabitStatus.Active).ToList();
        var listed = active.Where(habit => habit.StartsOn <= date).ToList();
        var upcoming = active.Where(habit => habit.StartsOn > date).OrderBy(habit => habit.StartsOn).ToList();
        var archivedCount = habits.Count - active.Count;

        if (listed.Count == 0)
        {
            return HabitResult<HabitsForDay>.Ok(new HabitsForDay(date, today, [], upcoming, archivedCount));
        }

        var from = HabitProgressCalculator.IsoWeekStart(Min(date, today.AddDays(-LoadedHistoryDays)));
        var checkIns = (await repository.GetCheckInsAsync(userId, listed.Select(habit => habit.Id).ToList(), from, today, cancellationToken))
            .ToLookup(checkIn => checkIn.HabitId);
        // One Hydration read for the (at most one) active water habit.
        var water = listed.Any(IsWater) ? await HabitWater.DaysAsync(hydration, userId, from, today, cancellationToken) : HabitWater.None;
        var items = new List<HabitListItem>();

        foreach (var habit in listed)
        {
            var habitWater = IsWater(habit) ? water : HabitWater.None;
            var calculator = new HabitProgressCalculator(habit, checkIns[habit.Id], today, from, TargetMet(habit, habitWater));
            var progress = calculator.Evaluate(HabitReads.DefaultWindowDays);

            if (progress.CurrentStreakIsPartial)
            {
                var all = await repository.GetCheckInsAsync(userId, [habit.Id], habit.StartsOn, today, cancellationToken);

                if (IsWater(habit))
                {
                    habitWater = await HabitWater.DaysAsync(hydration, userId, habit.StartsOn, today, cancellationToken);
                }

                calculator = new HabitProgressCalculator(habit, all, today, hydrationTargetMet: TargetMet(habit, habitWater));
                progress = calculator.Evaluate(HabitReads.DefaultWindowDays);
            }

            var status = calculator.StatusOn(date);
            var week = habit.VersionOn(date)?.Kind == HabitFrequencyKind.TimesPerWeek ? calculator.Week(date) : null;

            items.Add(new HabitListItem(habit, status, calculator.CheckInOn(date)?.Note, Group(status, week),
                progress.CurrentStreak, progress.StreakUnit, week, habitWater.GetValueOrDefault(date)));
        }

        return HabitResult<HabitsForDay>.Ok(new HabitsForDay(date, today, items, upcoming, archivedCount));
    }

    public static HabitListGroup Group(HabitDayStatus status, HabitWeekProgress? week) => status switch
    {
        HabitDayStatus.Done or HabitDayStatus.Excused => HabitListGroup.Done,
        HabitDayStatus.Inactive => HabitListGroup.NotToday,
        _ when week is not null => week.IsMet ? HabitListGroup.Done : week.Target == 0 ? HabitListGroup.NotToday : HabitListGroup.ToDo,
        HabitDayStatus.Pending or HabitDayStatus.Missed => HabitListGroup.ToDo,
        _ => HabitListGroup.NotToday
    };

    private static DateOnly Min(DateOnly first, DateOnly second) => first < second ? first : second;

    private static bool IsWater(Habit habit) => habit.CompletionSource == HabitCompletionSource.Hydration;

    private static IReadOnlyDictionary<DateOnly, bool>? TargetMet(Habit habit, IReadOnlyDictionary<DateOnly, HydrationDay> water) =>
        IsWater(habit) ? HabitWater.TargetMet(water) : null;
}

// One habit with its statistics (adherence over 7, 30 or 90 days) and the last five weeks.
public sealed class GetHabitDetailHandler(IHabitRepository repository, IHydrationDayReader hydration, TimeProvider clock)
{
    public static readonly IReadOnlyList<int> WindowDays = [7, 30, 90];

    public async Task<HabitResult<HabitDetail>> HandleAsync(Guid userId, Guid habitId, int utcOffsetMinutes, int windowDays,
        CancellationToken cancellationToken)
    {
        DateOnly today;

        try
        {
            today = HabitLimits.LocalToday(clock, utcOffsetMinutes);

            if (!WindowDays.Contains(windowDays))
            {
                throw new ArgumentOutOfRangeException("window", "Choose 7, 30 or 90 days.");
            }
        }
        catch (ArgumentException exception)
        {
            return HabitResult<HabitDetail>.Invalid(exception);
        }

        return await repository.GetAsync(userId, habitId, cancellationToken) is { } habit
            ? HabitResult<HabitDetail>.Ok(await HabitReads.DetailAsync(repository, hydration, habit, today, windowDays, cancellationToken))
            : HabitResult<HabitDetail>.NotFound();
    }
}

// The statuses of from..to (at most 366 days, not after today).
public sealed class GetHabitHistoryHandler(IHabitRepository repository, IHydrationDayReader hydration, TimeProvider clock)
{
    public const int MaxDays = 366;

    public async Task<HabitResult<HabitHistory>> HandleAsync(Guid userId, Guid habitId, DateOnly from, DateOnly to, int utcOffsetMinutes,
        CancellationToken cancellationToken)
    {
        DateOnly today;

        try
        {
            today = HabitLimits.LocalToday(clock, utcOffsetMinutes);

            if (to > today)
            {
                throw new ArgumentOutOfRangeException(nameof(to), "The history can't include future dates.");
            }

            if (from > to)
            {
                throw new ArgumentOutOfRangeException(nameof(from), "The first date must not be after the last.");
            }

            if (to.DayNumber - from.DayNumber + 1 > MaxDays)
            {
                throw new ArgumentOutOfRangeException(nameof(from), $"Ask for at most {MaxDays} days at a time.");
            }
        }
        catch (ArgumentException exception)
        {
            return HabitResult<HabitHistory>.Invalid(exception);
        }

        if (await repository.GetAsync(userId, habitId, cancellationToken) is not { } habit)
        {
            return HabitResult<HabitHistory>.NotFound();
        }

        // Whole weeks, so the week goals of the first days are complete.
        var loadFrom = HabitProgressCalculator.IsoWeekStart(from);
        var checkIns = await repository.GetCheckInsAsync(userId, [habit.Id], loadFrom, to, cancellationToken);
        var water = habit.CompletionSource == HabitCompletionSource.Hydration
            ? HabitWater.TargetMet(await HabitWater.DaysAsync(hydration, userId, loadFrom, to, cancellationToken))
            : null;
        var calculator = new HabitProgressCalculator(habit, checkIns, today, loadFrom, water);

        return HabitResult<HabitHistory>.Ok(new HabitHistory(habit, from, to, HabitReads.Entries(calculator, from, to)));
    }
}

// Archived habits, most recently archived first.
public sealed class GetArchivedHabitsHandler(IHabitRepository repository)
{
    public async Task<IReadOnlyList<Habit>> HandleAsync(Guid userId, CancellationToken cancellationToken) =>
        (await repository.GetAllAsync(userId, cancellationToken))
            .Where(habit => habit.Status == HabitStatus.Archived)
            .OrderByDescending(habit => habit.ArchivedAtUtc)
            .ToList();
}

internal static class HabitReads
{
    public const int DefaultWindowDays = 30;

    // The calendar grid: the current ISO week and the four before it.
    public const int RecentWeeks = 5;

    public static async Task<HabitDetail> DetailAsync(IHabitRepository repository, IHydrationDayReader hydration, Habit habit, DateOnly today,
        int windowDays, CancellationToken cancellationToken)
    {
        var (calculator, water) = await FullHistoryAsync(repository, hydration, habit, today, cancellationToken);
        var recentFrom = HabitProgressCalculator.IsoWeekStart(today).AddDays(-7 * (RecentWeeks - 1));

        return new HabitDetail(habit, calculator.Evaluate(windowDays), recentFrom, Entries(calculator, recentFrom, today),
            await WaterOnAsync(hydration, habit, water, today, cancellationToken));
    }

    public static async Task<HabitDayResult> DayAsync(IHabitRepository repository, IHydrationDayReader hydration, Habit habit, DateOnly date,
        DateOnly today, CancellationToken cancellationToken)
    {
        var (calculator, water) = await FullHistoryAsync(repository, hydration, habit, today, cancellationToken);

        return new HabitDayResult(habit, new HabitDayEntry(date, calculator.StatusOn(date), calculator.CheckInOn(date)?.Note),
            calculator.Evaluate(DefaultWindowDays), await WaterOnAsync(hydration, habit, water, date, cancellationToken));
    }

    public static IReadOnlyList<HabitDayEntry> Entries(HabitProgressCalculator calculator, DateOnly from, DateOnly to) =>
        calculator.Days(from, to).Select(day => new HabitDayEntry(day.Date, day.Status, calculator.CheckInOn(day.Date)?.Note)).ToList();

    // Personal histories are small (HAB-001 §7.2): the best streak needs all of it. A water habit's days
    // come from Hydration over the same range, so an edited or deleted past intake re-derives its days.
    private static async Task<(HabitProgressCalculator Calculator, IReadOnlyDictionary<DateOnly, HydrationDay> Water)> FullHistoryAsync(
        IHabitRepository repository, IHydrationDayReader hydration, Habit habit, DateOnly today, CancellationToken cancellationToken)
    {
        if (habit.StartsOn > today)
        {
            return (new HabitProgressCalculator(habit, [], today), HabitWater.None);
        }

        var checkIns = await repository.GetCheckInsAsync(habit.UserId, [habit.Id], habit.StartsOn, today, cancellationToken);

        if (habit.CompletionSource != HabitCompletionSource.Hydration)
        {
            return (new HabitProgressCalculator(habit, checkIns, today), HabitWater.None);
        }

        var water = await HabitWater.DaysAsync(hydration, habit.UserId, habit.StartsOn, today, cancellationToken);

        return (new HabitProgressCalculator(habit, checkIns, today, hydrationTargetMet: HabitWater.TargetMet(water)), water);
    }

    // The water day shown with a Hydration habit (also before its start: "1250 / 2000 ml" today).
    private static async Task<HydrationDay?> WaterOnAsync(IHydrationDayReader hydration, Habit habit,
        IReadOnlyDictionary<DateOnly, HydrationDay> water, DateOnly date, CancellationToken cancellationToken)
    {
        if (habit.CompletionSource != HabitCompletionSource.Hydration)
        {
            return null;
        }

        return water.TryGetValue(date, out var day) ? day : (await hydration.GetDaysAsync(habit.UserId, date, date, cancellationToken)).Single();
    }
}
