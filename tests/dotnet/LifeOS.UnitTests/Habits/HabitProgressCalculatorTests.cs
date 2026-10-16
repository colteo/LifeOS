using LifeOS.Domain.Habits;

namespace LifeOS.UnitTests.Habits;

// HAB-001 §6: day statuses, streaks, week goals and adherence. Today is Wednesday 14 Oct 2026 unless a
// test says otherwise; Monday 12 Oct starts the current ISO week.
public class HabitProgressCalculatorTests
{
    private static readonly Guid User = new("01a0ea76-0c00-7000-8000-0000000000a1");
    private static readonly DateOnly Today = new(2026, 10, 14);
    private static readonly DateOnly Far = new(2030, 1, 1);
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static readonly HabitFrequency MonWedFri =
        HabitFrequency.OnWeekdays(WeekdaySet.Of(DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday));

    private static DateOnly D(int month, int day) => new(2026, month, day);

    private static Habit Create(HabitFrequency frequency, DateOnly startsOn, HabitCompletionSource source = HabitCompletionSource.Manual) =>
        Habit.Create(User, "Habit", null, source, frequency, startsOn, startsOn, Now);

    private static IEnumerable<DateOnly> Range(DateOnly from, DateOnly to)
    {
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    private static List<HabitCheckIn> Done(Habit habit, IEnumerable<DateOnly> dates) =>
        dates.Select(date => habit.CheckIn(date, HabitCheckInStatus.Done, null, Far, Now)).ToList();

    private static List<HabitCheckIn> Done(Habit habit, params DateOnly[] dates) => Done(habit, dates.AsEnumerable());

    private static List<HabitCheckIn> Excused(Habit habit, params IEnumerable<DateOnly> dates) =>
        dates.Select(date => habit.CheckIn(date, HabitCheckInStatus.Excused, null, Far, Now)).ToList();

    private static HabitProgress Evaluate(Habit habit, IEnumerable<HabitCheckIn> checkIns, int window = 30, DateOnly? today = null) =>
        new HabitProgressCalculator(habit, checkIns, today ?? Today).Evaluate(window);

    // ---- Day status (§6.1) ----

    [Fact]
    public void Status_Daily()
    {
        var habit = Create(HabitFrequency.Daily, D(10, 10));
        var calculator = new HabitProgressCalculator(habit, [.. Done(habit, D(10, 11)), .. Excused(habit, D(10, 12))], Today);

        Assert.Equal(HabitDayStatus.Inactive, calculator.StatusOn(D(10, 9)));
        Assert.Equal(HabitDayStatus.Missed, calculator.StatusOn(D(10, 10)));
        Assert.Equal(HabitDayStatus.Done, calculator.StatusOn(D(10, 11)));
        Assert.Equal(HabitDayStatus.Excused, calculator.StatusOn(D(10, 12)));
        Assert.Equal(HabitDayStatus.Missed, calculator.StatusOn(D(10, 13)));
        Assert.Equal(HabitDayStatus.Pending, calculator.StatusOn(Today));
    }

    [Fact]
    public void Status_Weekdays_NotScheduledAndBonus()
    {
        var habit = Create(MonWedFri, D(10, 5));
        var calculator = new HabitProgressCalculator(habit, Done(habit, D(10, 13)), Today);

        Assert.Equal(HabitDayStatus.Missed, calculator.StatusOn(D(10, 12)));
        Assert.Equal(HabitDayStatus.Done, calculator.StatusOn(D(10, 13)));   // Tuesday: a bonus Done
        Assert.Equal(HabitDayStatus.NotScheduled, calculator.StatusOn(D(10, 11)));
        Assert.Equal(HabitDayStatus.Pending, calculator.StatusOn(Today));
        Assert.Equal(HabitDayStatus.NotScheduled, new HabitProgressCalculator(habit, [], D(10, 13)).StatusOn(D(10, 13)));
    }

    [Fact]
    public void Status_TimesPerWeek_DaysWithoutCheckInAreNeutral_TodayIsPending()
    {
        var habit = Create(HabitFrequency.PerWeek(3), D(10, 5));
        var calculator = new HabitProgressCalculator(habit, Done(habit, D(10, 6)), Today);

        Assert.Equal(HabitDayStatus.NotScheduled, calculator.StatusOn(D(10, 5)));
        Assert.Equal(HabitDayStatus.Done, calculator.StatusOn(D(10, 6)));
        Assert.Equal(HabitDayStatus.Pending, calculator.StatusOn(Today));
    }

    [Fact]
    public void Status_ArchivedPeriod_IsInactive()
    {
        var habit = Create(HabitFrequency.Daily, D(10, 1));
        habit.Archive(D(10, 6), Now);
        habit.Restore(D(10, 10), null, Now);
        var calculator = new HabitProgressCalculator(habit, [], Today);

        Assert.Equal(
            [HabitDayStatus.Missed, HabitDayStatus.Inactive, HabitDayStatus.Inactive, HabitDayStatus.Inactive, HabitDayStatus.Inactive, HabitDayStatus.Missed],
            calculator.Days(D(10, 5), D(10, 10)).Select(day => day.Status));
    }

    [Fact]
    public void Days_AreCappedAtToday()
    {
        var habit = Create(HabitFrequency.Daily, D(10, 13));
        var days = new HabitProgressCalculator(habit, [], Today).Days(D(10, 12), D(10, 20));

        Assert.Equal(
            [new HabitDay(D(10, 12), HabitDayStatus.Inactive), new HabitDay(D(10, 13), HabitDayStatus.Missed), new HabitDay(Today, HabitDayStatus.Pending)],
            days);
    }

    // ---- Day-based streaks (§6.2) ----

    [Fact]
    public void DailyStreak_TodayPendingIsSkipped_DoneTodayCounts_MissedStops()
    {
        var habit = Create(HabitFrequency.Daily, D(10, 4));
        var checkIns = Done(habit, D(10, 11), D(10, 12), D(10, 13));

        var pending = Evaluate(habit, checkIns);
        Assert.Equal((HabitDayStatus.Pending, 3, 3, HabitStreakUnit.Day), (pending.TodayStatus, pending.CurrentStreak, pending.BestStreak, pending.StreakUnit));
        Assert.Null(pending.ThisWeek);

        var doneToday = Evaluate(habit, [.. checkIns, .. Done(habit, Today)]);
        Assert.Equal((HabitDayStatus.Done, 4, 4), (doneToday.TodayStatus, doneToday.CurrentStreak, doneToday.BestStreak));
    }

    [Fact]
    public void DailyStreak_ExcusedIsNeutral()
    {
        var habit = Create(HabitFrequency.Daily, D(10, 4));
        var progress = Evaluate(habit, [.. Done(habit, D(10, 10), D(10, 12), D(10, 13)), .. Excused(habit, D(10, 11))]);

        Assert.Equal(3, progress.CurrentStreak);
    }

    [Fact]
    public void DailyStreak_ContinuesAcrossAnArchivedPeriod()
    {
        var habit = Create(HabitFrequency.Daily, D(10, 1));
        habit.Archive(D(10, 6), Now);
        habit.Restore(D(10, 10), null, Now);

        var progress = Evaluate(habit, Done(habit, [.. Range(D(10, 1), D(10, 5)), .. Range(D(10, 10), D(10, 13))]));

        Assert.Equal((9, 9), (progress.CurrentStreak, progress.BestStreak));
    }

    [Fact]
    public void WeekdaysStreak_SkipsOffDays_IgnoresBonus_AndBestKeepsTheLongestRun()
    {
        var habit = Create(MonWedFri, D(9, 28));
        var all = new[] { D(9, 28), D(9, 30), D(10, 2), D(10, 5), D(10, 7), D(10, 9), D(10, 12) };

        var unbroken = Evaluate(habit, [.. Done(habit, all), .. Done(habit, D(10, 13))]);
        Assert.Equal((7, 7), (unbroken.CurrentStreak, unbroken.BestStreak));

        // Missing Wednesday 7 Oct: the streak restarts after it; the best run is the first four.
        var broken = Evaluate(habit, Done(habit, all.Where(date => date != D(10, 7))));
        Assert.Equal((2, 4), (broken.CurrentStreak, broken.BestStreak));
    }

    [Fact]
    public void DailyToWeekdays_IsOneSegment()
    {
        var habit = Create(HabitFrequency.Daily, D(10, 5));
        habit.ChangeFrequency(MonWedFri, D(10, 12), Now);

        var progress = Evaluate(habit, Done(habit, [.. Range(D(10, 5), D(10, 12))]));

        Assert.Equal((8, HabitStreakUnit.Day), (progress.CurrentStreak, progress.StreakUnit));
    }

    // ---- Week-based (§6.3) ----

    [Fact]
    public void Weekly_PartialFirstWeek_HasAProportionalTarget_CurrentWeekIsPending()
    {
        // Starts Saturday 10 Oct with N = 3: that week only has two days, so its target is 2.
        var habit = Create(HabitFrequency.PerWeek(3), D(10, 10));
        var checkIns = Done(habit, D(10, 10), D(10, 11), D(10, 12));

        var progress = Evaluate(habit, checkIns);

        Assert.Equal(new HabitWeekProgress(D(10, 5), 2, 2), new HabitProgressCalculator(habit, checkIns, Today).Week(D(10, 10)));
        Assert.Equal((1, 1, HabitStreakUnit.Week), (progress.CurrentStreak, progress.BestStreak, progress.StreakUnit));
        Assert.Equal(new HabitWeekProgress(D(10, 12), 1, 3), progress.ThisWeek);
        Assert.Equal(new HabitAdherence(2, 2, 100, 30), progress.Adherence);

        var met = Evaluate(habit, [.. checkIns, .. Done(habit, D(10, 13), Today)]);
        Assert.Equal((2, 2), (met.CurrentStreak, met.BestStreak));
        Assert.True(met.ThisWeek!.IsMet);
        Assert.Equal(new HabitAdherence(5, 5, 100, 30), met.Adherence);
    }

    [Fact]
    public void Weekly_ExtrasAreKept_ButCappedInAdherence_AndAMissedWeekStops()
    {
        var habit = Create(HabitFrequency.PerWeek(3), D(9, 28));
        var checkIns = Done(habit, D(9, 28), D(9, 29), D(9, 30), D(10, 1), D(10, 6), D(10, 12), D(10, 13), Today);

        var progress = Evaluate(habit, checkIns);
        var calculator = new HabitProgressCalculator(habit, checkIns, Today);

        Assert.Equal(new HabitWeekProgress(D(9, 28), 4, 3), calculator.Week(D(10, 4)));
        Assert.Equal((1, 1), (progress.CurrentStreak, progress.BestStreak));
        Assert.Equal(new HabitWeekProgress(D(10, 12), 3, 3), progress.ThisWeek);
        // 3/3 + 1/3 + 3/3 (current week, met).
        Assert.Equal(new HabitAdherence(7, 9, 78, 30), progress.Adherence);
    }

    [Fact]
    public void Weekly_ExcusedDaysReduceTheTarget_AndAFullyExcusedWeekIsSkipped()
    {
        var habit = Create(HabitFrequency.PerWeek(3), D(9, 28));
        var firstWeek = Done(habit, D(9, 28), D(9, 29), D(9, 30));

        // Five excused days leave two available: target 2, met with Saturday and Sunday.
        var reduced = new HabitProgressCalculator(habit,
            [.. Excused(habit, Range(D(10, 5), D(10, 9))), .. Done(habit, D(10, 10), D(10, 11))], Today);
        Assert.Equal(new HabitWeekProgress(D(10, 5), 2, 2), reduced.Week(D(10, 5)));

        // All seven excused: target 0, neither breaks nor extends.
        var skipped = Evaluate(habit, [.. firstWeek, .. Excused(habit, Range(D(10, 5), D(10, 11)))]);
        Assert.Equal((1, 1), (skipped.CurrentStreak, skipped.BestStreak));
        Assert.Equal(new HabitAdherence(3, 3, 100, 30), skipped.Adherence);
    }

    [Fact]
    public void DayToWeekChange_EndsTheWalk()
    {
        var habit = Create(HabitFrequency.Daily, D(9, 28));
        habit.ChangeFrequency(HabitFrequency.PerWeek(3), D(10, 12), Now);

        var progress = Evaluate(habit, Done(habit, [.. Range(D(9, 28), Today)]));

        // Fourteen Daily days in a row do not add to the weekly streak.
        Assert.Equal((1, 1, HabitStreakUnit.Week), (progress.CurrentStreak, progress.BestStreak, progress.StreakUnit));
    }

    [Fact]
    public void DayToWeekChange_MidWeek_JudgesOnlyTheWeeklyDays()
    {
        var habit = Create(HabitFrequency.Daily, D(10, 5));
        habit.ChangeFrequency(HabitFrequency.PerWeek(2), Today, Now);

        var progress = Evaluate(habit, Done(habit, [.. Range(D(10, 5), Today)]));

        // Wednesday-Sunday are weekly days (target min(2, 5) = 2); Monday and Tuesday were Daily.
        Assert.Equal(new HabitWeekProgress(D(10, 12), 1, 2), progress.ThisWeek);
        Assert.Equal((0, HabitStreakUnit.Week), (progress.CurrentStreak, progress.StreakUnit));
    }

    [Fact]
    public void WeekToDayChange_EndsTheWalk()
    {
        var habit = Create(HabitFrequency.PerWeek(1), D(9, 28));
        habit.ChangeFrequency(HabitFrequency.Daily, D(10, 12), Now);

        var progress = Evaluate(habit, Done(habit, D(9, 28), D(10, 5), D(10, 12), D(10, 13)));

        Assert.Equal((2, 2, HabitStreakUnit.Day), (progress.CurrentStreak, progress.BestStreak, progress.StreakUnit));
    }

    [Fact]
    public void IsoWeeks_AcrossTheYearEnd()
    {
        var today = new DateOnly(2027, 1, 6);
        var habit = Create(HabitFrequency.PerWeek(2), D(12, 21));
        var checkIns = Done(habit, D(12, 22), D(12, 24), D(12, 31), new DateOnly(2027, 1, 2));
        var calculator = new HabitProgressCalculator(habit, checkIns, today);

        Assert.Equal(D(12, 28), HabitProgressCalculator.IsoWeekStart(new DateOnly(2027, 1, 3)));
        Assert.Equal(D(12, 28), HabitProgressCalculator.IsoWeekStart(D(12, 28)));
        Assert.Equal(new DateOnly(2027, 1, 4), HabitProgressCalculator.IsoWeekStart(new DateOnly(2027, 1, 4)));
        Assert.Equal(new HabitWeekProgress(D(12, 28), 2, 2), calculator.Week(new DateOnly(2027, 1, 1)));

        var progress = calculator.Evaluate(30);
        Assert.Equal(new HabitWeekProgress(new DateOnly(2027, 1, 4), 0, 2), progress.ThisWeek);
        Assert.Equal(2, progress.CurrentStreak);
    }

    // ---- Adherence (§6.4) ----

    [Fact]
    public void Adherence_DayBased_ExcludesExcusedAndPending()
    {
        var habit = Create(HabitFrequency.Daily, D(9, 1));
        var checkIns = new List<HabitCheckIn>([.. Done(habit, D(10, 8), D(10, 9), D(10, 10)), .. Excused(habit, D(10, 11))]);

        Assert.Equal(new HabitAdherence(3, 5, 60, 7), Evaluate(habit, checkIns, window: 7).Adherence);
        Assert.Equal(new HabitAdherence(3, 28, 11, 30), Evaluate(habit, checkIns, window: 30).Adherence);
    }

    [Fact]
    public void Adherence_RoundsHalfAwayFromZero_AndExcludesBonus()
    {
        var habit = Create(HabitFrequency.Daily, D(10, 6));
        Assert.Equal(new HabitAdherence(1, 8, 13, 30), Evaluate(habit, Done(habit, D(10, 6))).Adherence);

        var weekdays = Create(MonWedFri, D(10, 12));
        Assert.Equal(new HabitAdherence(1, 1, 100, 30), Evaluate(weekdays, Done(weekdays, D(10, 12), D(10, 13))).Adherence);
    }

    [Fact]
    public void Adherence_WithoutData_HasNoPercentage()
    {
        var habit = Create(HabitFrequency.Daily, Today);

        var progress = Evaluate(habit, []);

        Assert.Equal(new HabitAdherence(0, 0, null, 30), progress.Adherence);
        Assert.Equal((0, 0), (progress.CurrentStreak, progress.BestStreak));
        Assert.Throws<ArgumentOutOfRangeException>(() => Evaluate(habit, [], window: 0));
    }

    [Fact]
    public void Adherence_WeeksMustStartInsideTheWindow()
    {
        var habit = Create(HabitFrequency.PerWeek(1), D(9, 28));
        var checkIns = Done(habit, D(9, 28), D(10, 5));

        // Window 7 = 8-14 Oct: neither past week starts inside it; the current week is pending.
        Assert.Equal(new HabitAdherence(0, 0, null, 7), Evaluate(habit, checkIns, window: 7).Adherence);
        Assert.Equal(new HabitAdherence(2, 2, 100, 30), Evaluate(habit, checkIns, window: 30).Adherence);
    }

    [Fact]
    public void UpcomingHabit_HasNoProgress()
    {
        var habit = Create(HabitFrequency.PerWeek(2), Today.AddDays(3));

        var progress = Evaluate(habit, []);

        Assert.Equal((HabitDayStatus.Inactive, 0, HabitStreakUnit.Week), (progress.TodayStatus, progress.CurrentStreak, progress.StreakUnit));
    }

    // ---- Hydration derivation (§6.5) ----

    [Fact]
    public void Hydration_DoneIsDerived_ExcusedWins_NoTargetIsNotScheduled()
    {
        var habit = Create(HabitFrequency.Daily, D(10, 10), HabitCompletionSource.Hydration);
        var water = new Dictionary<DateOnly, bool>
        {
            [D(10, 10)] = true,
            [D(10, 11)] = false,
            [D(10, 13)] = true,
            [Today] = false
        };

        var calculator = new HabitProgressCalculator(habit, Excused(habit, D(10, 11)), Today, hydrationTargetMet: water);

        Assert.Equal(
            [HabitDayStatus.Done, HabitDayStatus.Excused, HabitDayStatus.NotScheduled, HabitDayStatus.Done, HabitDayStatus.Pending],
            calculator.Days(D(10, 10), Today).Select(day => day.Status));
        Assert.Equal(2, calculator.Evaluate(30).CurrentStreak);

        water[D(10, 12)] = false;
        Assert.Equal(1, new HabitProgressCalculator(habit, [], Today, hydrationTargetMet: water).Evaluate(30).CurrentStreak);
    }

    [Fact]
    public void Hydration_WeekGoal_CountsMetDays_DaysWithoutTargetAreNotAvailable()
    {
        // Week of Mon 12 Oct (today Wed 14): Mon met, Tue no target, today not met yet; 3 times a week.
        var habit = Create(HabitFrequency.PerWeek(3), D(10, 5), HabitCompletionSource.Hydration);
        var water = new Dictionary<DateOnly, bool> { [D(10, 12)] = true, [Today] = false };

        var week = new HabitProgressCalculator(habit, [], Today, hydrationTargetMet: water).Week(Today)!;
        Assert.Equal((1, 3), (week.Done, week.Target));

        // An excused day lowers the target too; a met day counts even when excused is elsewhere.
        water[D(10, 13)] = true;
        water[Today] = true;
        var met = new HabitProgressCalculator(habit, Excused(habit, D(10, 12)), Today, hydrationTargetMet: water).Week(Today)!;
        Assert.Equal((2, 3, false), (met.Done, met.Target, met.IsMet));

        // Without any water days every elapsed day is unavailable; the remaining days still allow the goal.
        var none = new HabitProgressCalculator(habit, [], Today).Week(Today)!;
        Assert.Equal((0, 3), (none.Done, none.Target));
    }

    // ---- Bounded history ----

    [Fact]
    public void PartialHistory_FlagsAStreakThatReachesTheLoadedStart()
    {
        var habit = Create(HabitFrequency.Daily, D(9, 1));
        var checkIns = Done(habit, [.. Range(D(9, 1), D(10, 13))]);

        var partial = new HabitProgressCalculator(habit, checkIns.Where(c => c.Date >= D(10, 5)), Today, historyFrom: D(10, 5)).Evaluate(7);
        Assert.Equal((9, true), (partial.CurrentStreak, partial.CurrentStreakIsPartial));

        var full = new HabitProgressCalculator(habit, checkIns, Today).Evaluate(7);
        Assert.Equal((43, 43, false), (full.CurrentStreak, full.BestStreak, full.CurrentStreakIsPartial));

        // A streak that ends inside the loaded range is exact.
        var ended = new HabitProgressCalculator(habit, checkIns.Where(c => c.Date >= D(10, 5) && c.Date != D(10, 8)), Today, historyFrom: D(10, 5)).Evaluate(7);
        Assert.Equal((5, false), (ended.CurrentStreak, ended.CurrentStreakIsPartial));
    }
}
