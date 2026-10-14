namespace LifeOS.Domain.Habits;

public sealed record HabitDay(DateOnly Date, HabitDayStatus Status);

// Met / Expected over the last WindowDays days ending today. Percent is null when nothing was expected.
public sealed record HabitAdherence(int Met, int Expected, int? Percent, int WindowDays);

// An ISO week (Monday-Sunday) of a TimesPerWeek habit: Done check-ins (extras included) and the week's
// target, min(N, available days).
public sealed record HabitWeekProgress(DateOnly WeekStart, int Done, int Target)
{
    public bool IsMet => Target > 0 && Done >= Target;
}

// CurrentStreakIsPartial: the check-ins were loaded only from a later date than StartsOn and the streak
// walk reached that limit, so the streak may be longer (the caller reloads the full history).
public sealed record HabitProgress(
    DateOnly AsOf,
    HabitDayStatus TodayStatus,
    int CurrentStreak,
    int BestStreak,
    HabitStreakUnit StreakUnit,
    HabitAdherence Adherence,
    HabitWeekProgress? ThisWeek,
    bool CurrentStreakIsPartial);

// Deterministic statistics of one habit (HAB-001 §6). Pure: no I/O, no clock; nothing it computes is
// stored. "today" is the caller's local date; every other date is a local calendar date.
//
// Day status (§6.1): before StartsOn or in an Inactive version → Inactive; a stored Done/Excused wins;
// a day a Weekdays habit does not expect → NotScheduled; today → Pending; a TimesPerWeek day without a
// check-in → NotScheduled (the week is judged, not the day); otherwise Missed.
//
// Streaks and adherence are computed over "units" in date order: one per day of a day-based version
// (Daily, Weekdays) and one per ISO week with TimesPerWeek days. A unit is Met, Broken, Pending (today /
// the current week, never breaks) or Neutral (excused, not scheduled, a bonus Done, a week with target
// 0). Inactive days produce no unit. A change between day-based and week-based starts a new segment:
// a streak never spans two segments (Daily ↔ Weekdays stay in one).
//
// Hydration-sourced habits (HAB-001 P-10) take the days' "target met" values as plain input: a date
// missing from the map has no target and is NotScheduled. Done is never stored for them.
public sealed class HabitProgressCalculator
{
    private readonly Habit _habit;
    private readonly HabitScheduleVersion[] _versions;
    private readonly Dictionary<DateOnly, HabitCheckIn> _checkIns;
    private readonly DateOnly _today;
    private readonly DateOnly _historyFrom;
    private readonly IReadOnlyDictionary<DateOnly, bool>? _hydrationTargetMet;

    // historyFrom: the first date whose check-ins are included (null: the whole history). It should be a
    // Monday, so every week from it on is complete.
    public HabitProgressCalculator(Habit habit, IEnumerable<HabitCheckIn> checkIns, DateOnly today,
        DateOnly? historyFrom = null, IReadOnlyDictionary<DateOnly, bool>? hydrationTargetMet = null)
    {
        ArgumentNullException.ThrowIfNull(habit);
        ArgumentNullException.ThrowIfNull(checkIns);

        _habit = habit;
        _versions = habit.Schedule.ToArray();
        _checkIns = checkIns.Where(checkIn => checkIn.HabitId == habit.Id).ToDictionary(checkIn => checkIn.Date);
        _today = today;
        _historyFrom = historyFrom is { } from && from > habit.StartsOn ? from : habit.StartsOn;
        _hydrationTargetMet = hydrationTargetMet;
    }

    // Monday of the ISO week containing the date.
    public static DateOnly IsoWeekStart(DateOnly date) => date.AddDays(-(WeekdaySet.Iso(date.DayOfWeek) - 1));

    public HabitCheckIn? CheckInOn(DateOnly date) => _checkIns.GetValueOrDefault(date);

    public HabitDayStatus StatusOn(DateOnly date)
    {
        var version = VersionOn(date);

        if (version is null || version.Kind == HabitFrequencyKind.Inactive)
        {
            return HabitDayStatus.Inactive;
        }

        var checkIn = _checkIns.GetValueOrDefault(date);

        if (checkIn?.Status == HabitCheckInStatus.Excused)
        {
            return HabitDayStatus.Excused;
        }

        if (_habit.CompletionSource == HabitCompletionSource.Manual)
        {
            if (checkIn?.Status == HabitCheckInStatus.Done)
            {
                return HabitDayStatus.Done;
            }
        }
        else
        {
            if (_hydrationTargetMet is null || !_hydrationTargetMet.TryGetValue(date, out var met))
            {
                return HabitDayStatus.NotScheduled;
            }

            if (met)
            {
                return HabitDayStatus.Done;
            }
        }

        if (version.Kind == HabitFrequencyKind.Weekdays && !version.Frequency.IsScheduledOn(date))
        {
            return HabitDayStatus.NotScheduled;
        }

        if (date == _today)
        {
            return HabitDayStatus.Pending;
        }

        return version.Kind == HabitFrequencyKind.TimesPerWeek ? HabitDayStatus.NotScheduled : HabitDayStatus.Missed;
    }

    // Every date from..to, capped at today (future days have no status yet).
    public IReadOnlyList<HabitDay> Days(DateOnly from, DateOnly to)
    {
        var last = to < _today ? to : _today;
        var days = new List<HabitDay>();

        for (var date = from; date <= last; date = date.AddDays(1))
        {
            days.Add(new HabitDay(date, StatusOn(date)));
        }

        return days;
    }

    // The week goal of the ISO week containing the date; null when no day of that week is TimesPerWeek.
    // Future days of the week count as available with the schedule in effect today.
    public HabitWeekProgress? Week(DateOnly date)
    {
        var weekStart = IsoWeekStart(date);
        int? timesPerWeek = null;
        int? firstTimesPerWeek = null;
        var days = 0;
        var excused = 0;
        var done = 0;

        for (var day = weekStart; day < weekStart.AddDays(7); day = day.AddDays(1))
        {
            if (VersionOn(day) is not { Kind: HabitFrequencyKind.TimesPerWeek } version)
            {
                continue;
            }

            days++;
            firstTimesPerWeek ??= version.TimesPerWeek;

            if (day <= _today)
            {
                // The goal in effect on the week's latest elapsed TimesPerWeek day.
                timesPerWeek = version.TimesPerWeek;

                switch (_checkIns.GetValueOrDefault(day)?.Status)
                {
                    case HabitCheckInStatus.Done:
                        done++;
                        break;
                    case HabitCheckInStatus.Excused:
                        excused++;
                        break;
                }
            }
        }

        if (days == 0)
        {
            return null;
        }

        var target = Math.Min((timesPerWeek ?? firstTimesPerWeek)!.Value, days - excused);

        return new HabitWeekProgress(weekStart, done, target);
    }

    public HabitProgress Evaluate(int windowDays)
    {
        if (windowDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(windowDays), "The window is at least one day.");
        }

        var units = Units();
        var unit = StreakUnit();
        var (current, partial) = CurrentStreak(units);

        return new HabitProgress(
            _today,
            StatusOn(_today),
            current,
            BestStreak(units, unit),
            unit,
            Adherence(units, windowDays),
            VersionOn(_today) is { Kind: HabitFrequencyKind.TimesPerWeek } ? Week(_today) : null,
            partial);
    }

    // ---- Units ----

    private enum Outcome
    {
        Met,
        Neutral,
        Broken,
        Pending
    }

    // Date: the day, or the week's Monday. Met/Expected: the unit's contribution to adherence.
    private sealed record Unit(DateOnly Date, bool WeekBased, int Segment, Outcome Outcome, int Met, int Expected);

    private List<Unit> Units()
    {
        var units = new List<Unit>();
        var emittedWeeks = new HashSet<DateOnly>();
        DateOnly? openWeek = null;
        bool? lastWeekBased = null;
        var segment = 0;

        void EmitWeek()
        {
            if (openWeek is not { } weekStart || !emittedWeeks.Add(weekStart))
            {
                openWeek = null;
                return;
            }

            var week = Week(weekStart)!;
            var outcome = week.Target == 0 ? Outcome.Neutral
                : week.IsMet ? Outcome.Met
                : weekStart <= _today && _today < weekStart.AddDays(7) ? Outcome.Pending
                : Outcome.Broken;

            units.Add(new Unit(weekStart, true, segment, outcome, Math.Min(week.Done, week.Target), week.Target));
            openWeek = null;
        }

        for (var date = _historyFrom; date <= _today; date = date.AddDays(1))
        {
            var version = VersionOn(date)!;

            if (version.Kind == HabitFrequencyKind.Inactive)
            {
                continue;
            }

            var weekBased = version.Kind == HabitFrequencyKind.TimesPerWeek;

            if (weekBased)
            {
                if (openWeek is { } week && week != IsoWeekStart(date))
                {
                    EmitWeek();
                }
            }
            else
            {
                EmitWeek();
            }

            if (lastWeekBased is { } previous && previous != weekBased)
            {
                segment++;
            }

            lastWeekBased = weekBased;

            if (weekBased)
            {
                openWeek = IsoWeekStart(date);
                continue;
            }

            var status = StatusOn(date);
            // A Done on a day a Weekdays habit does not expect is a bonus: shown, but neutral here.
            var outcome = !version.Frequency.IsScheduledOn(date) ? Outcome.Neutral : status switch
            {
                HabitDayStatus.Done => Outcome.Met,
                HabitDayStatus.Missed => Outcome.Broken,
                HabitDayStatus.Pending => Outcome.Pending,
                _ => Outcome.Neutral
            };

            units.Add(new Unit(date, false, segment, outcome,
                outcome == Outcome.Met ? 1 : 0,
                outcome is Outcome.Met or Outcome.Broken ? 1 : 0));
        }

        EmitWeek();

        return units;
    }

    // The unit of the schedule in effect today, or of the latest active version before it (archived);
    // of the first version for a habit that has not started.
    private HabitStreakUnit StreakUnit()
    {
        var reference = _versions.LastOrDefault(version => version.EffectiveFrom <= _today && version.Kind != HabitFrequencyKind.Inactive)
            ?? _versions.FirstOrDefault(version => version.Kind != HabitFrequencyKind.Inactive);

        return reference?.Kind == HabitFrequencyKind.TimesPerWeek ? HabitStreakUnit.Week : HabitStreakUnit.Day;
    }

    // Walks back from today: Pending and Neutral are skipped, Met counts, Broken or a segment change stops.
    private (int Streak, bool Partial) CurrentStreak(List<Unit> units)
    {
        if (units.Count == 0)
        {
            return (0, false);
        }

        var segment = units[^1].Segment;
        var streak = 0;

        for (var index = units.Count - 1; index >= 0; index--)
        {
            var unit = units[index];

            if (unit.Segment != segment || unit.Outcome == Outcome.Broken)
            {
                return (streak, false);
            }

            if (unit.Outcome == Outcome.Met)
            {
                streak++;
            }
        }

        // Every loaded unit was walked: older, unloaded history may continue the streak.
        return (streak, _historyFrom > _habit.StartsOn);
    }

    // The longest run with the same rules, among the segments of the current unit (days or weeks).
    private static int BestStreak(List<Unit> units, HabitStreakUnit streakUnit)
    {
        var weekBased = streakUnit == HabitStreakUnit.Week;
        var best = 0;
        var run = 0;
        int? segment = null;

        foreach (var unit in units)
        {
            if (unit.Segment != segment)
            {
                segment = unit.Segment;
                run = 0;
            }

            if (unit.WeekBased != weekBased)
            {
                continue;
            }

            switch (unit.Outcome)
            {
                case Outcome.Met:
                    run++;
                    best = Math.Max(best, run);
                    break;
                case Outcome.Broken:
                    run = 0;
                    break;
            }
        }

        return best;
    }

    // Day units inside the window; week units whose week starts inside the window, the current week only
    // once met (Pending is skipped). Excused days are in neither count.
    private HabitAdherence Adherence(List<Unit> units, int windowDays)
    {
        var windowStart = _today.AddDays(1 - windowDays);
        var met = 0;
        var expected = 0;

        foreach (var unit in units)
        {
            if (unit.Date < windowStart || unit.Outcome == Outcome.Pending)
            {
                continue;
            }

            met += unit.Met;
            expected += unit.Expected;
        }

        int? percent = expected == 0
            ? null
            : (int)Math.Round(100m * met / expected, MidpointRounding.AwayFromZero);

        return new HabitAdherence(met, expected, percent, windowDays);
    }

    private HabitScheduleVersion? VersionOn(DateOnly date)
    {
        HabitScheduleVersion? found = null;

        foreach (var version in _versions)
        {
            if (version.EffectiveFrom > date)
            {
                break;
            }

            found = version;
        }

        return found;
    }
}
