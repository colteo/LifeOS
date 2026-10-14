using LifeOS.Domain.Habits;

namespace LifeOS.UnitTests.Habits;

// HAB-001 §5: the Habit aggregate, its schedule versions and the check-in rules. Wednesday 14 Oct 2026
// is "today" unless a test moves it.
public class HabitTests
{
    private static readonly Guid User = new("01a0ea76-0c00-7000-8000-0000000000a1");
    private static readonly DateOnly Today = new(2026, 10, 14);
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);

    private static readonly HabitFrequency MonWedFri =
        HabitFrequency.OnWeekdays(WeekdaySet.Of(DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday));

    private static Habit Create(HabitFrequency? frequency = null, DateOnly? startsOn = null,
        HabitCompletionSource source = HabitCompletionSource.Manual) =>
        Habit.Create(User, "Read 20 pages", null, source, frequency ?? HabitFrequency.Daily, startsOn ?? Today, Today, Now);

    private static (DateOnly, HabitFrequencyKind)[] Versions(Habit habit) =>
        habit.Schedule.Select(version => (version.EffectiveFrom, version.Kind)).ToArray();

    // ---- Create ----

    [Fact]
    public void Create_TrimsText_StartsActive_WithOneVersionFromStartsOn()
    {
        var habit = Habit.Create(User, "  Stretch  ", "  Ten minutes ", HabitCompletionSource.Manual, MonWedFri,
            Today.AddDays(-3), Today, Now.AddTicks(1234567));

        Assert.NotEqual(Guid.Empty, habit.Id);
        Assert.Equal(7, habit.Id.Version);
        Assert.Equal((User, "Stretch", "Ten minutes"), (habit.UserId, habit.Name, habit.Description));
        Assert.Equal((HabitStatus.Active, (DateTimeOffset?)null), (habit.Status, habit.ArchivedAtUtc));
        Assert.Equal(Today.AddDays(-3), habit.StartsOn);
        Assert.Equal([(Today.AddDays(-3), HabitFrequencyKind.Weekdays)], Versions(habit));
        Assert.Equal(MonWedFri, habit.CurrentFrequency);
        Assert.Equal(0, habit.CreatedAtUtc.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.Equal(habit.CreatedAtUtc, habit.UpdatedAtUtc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RequiresAName(string name)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Habit.Create(User, name, null, HabitCompletionSource.Manual, HabitFrequency.Daily, Today, Today, Now));

        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void Create_Bounds_NameAndDescription_WithoutCutting()
    {
        var longest = Habit.Create(User, new string('n', Habit.NameMaxLength), new string('d', Habit.DescriptionMaxLength),
            HabitCompletionSource.Manual, HabitFrequency.Daily, Today, Today, Now);
        Assert.Equal(Habit.NameMaxLength, longest.Name.Length);
        Assert.Equal(Habit.DescriptionMaxLength, longest.Description!.Length);

        Assert.Equal("name", Assert.Throws<ArgumentException>(() => Habit.Create(User, new string('n', Habit.NameMaxLength + 1), null,
            HabitCompletionSource.Manual, HabitFrequency.Daily, Today, Today, Now)).ParamName);
        Assert.Equal("description", Assert.Throws<ArgumentException>(() => Habit.Create(User, "Ok", new string('d', Habit.DescriptionMaxLength + 1),
            HabitCompletionSource.Manual, HabitFrequency.Daily, Today, Today, Now)).ParamName);
        Assert.Null(Habit.Create(User, "Ok", "   ", HabitCompletionSource.Manual, HabitFrequency.Daily, Today, Today, Now).Description);
    }

    [Fact]
    public void Create_StartsOn_WithinAYearOfToday()
    {
        Assert.Equal(Today.AddDays(-365), Create(startsOn: Today.AddDays(-365)).StartsOn);
        Assert.Equal(Today.AddDays(365), Create(startsOn: Today.AddDays(365)).StartsOn);

        Assert.Equal("startsOn", Assert.Throws<ArgumentOutOfRangeException>(() => Create(startsOn: Today.AddDays(-366))).ParamName);
        Assert.Equal("startsOn", Assert.Throws<ArgumentOutOfRangeException>(() => Create(startsOn: Today.AddDays(366))).ParamName);
    }

    [Fact]
    public void Create_RejectsInactive_UnknownSource_AndEmptyUser()
    {
        Assert.Throws<ArgumentException>(() => Create(HabitFrequency.Inactive));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(source: (HabitCompletionSource)9));
        Assert.Throws<ArgumentException>(() =>
            Habit.Create(Guid.Empty, "X", null, HabitCompletionSource.Manual, HabitFrequency.Daily, Today, Today, Now));
    }

    // ---- Frequency shape ----

    [Fact]
    public void Frequency_Shapes()
    {
        Assert.Equal(HabitFrequencyKind.Daily, HabitFrequency.Daily.Kind);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday], MonWedFri.Weekdays!.Value.Days);
        Assert.Equal(1 | 4 | 16, MonWedFri.Weekdays!.Value.Mask);
        Assert.Equal(3, HabitFrequency.PerWeek(3).TimesPerWeek);
        Assert.Equal(HabitFrequency.PerWeek(3), HabitFrequency.PerWeek(3));
        Assert.NotEqual(HabitFrequency.PerWeek(3), HabitFrequency.PerWeek(4));

        Assert.Throws<ArgumentException>(() => HabitFrequency.OnWeekdays(WeekdaySet.Of()));
        Assert.Throws<ArgumentOutOfRangeException>(() => HabitFrequency.PerWeek(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HabitFrequency.PerWeek(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => WeekdaySet.FromMask(128));

        Assert.Equal(MonWedFri, HabitFrequency.From(HabitFrequencyKind.Weekdays, 21, null));
        Assert.Throws<ArgumentException>(() => HabitFrequency.From(HabitFrequencyKind.Daily, 1, null));
        Assert.Throws<ArgumentException>(() => HabitFrequency.From(HabitFrequencyKind.Weekdays, null, null));
        Assert.Throws<ArgumentException>(() => HabitFrequency.From(HabitFrequencyKind.TimesPerWeek, 3, 3));

        // ISO: Monday 1 ... Sunday 64.
        Assert.Equal(64, WeekdaySet.Of(DayOfWeek.Sunday).Mask);
        Assert.True(WeekdaySet.FromMask(127).Contains(DayOfWeek.Sunday));
    }

    // ---- Versions ----

    [Fact]
    public void ChangeFrequency_AppendsFromToday_AndNeverChangesThePast()
    {
        var habit = Create(startsOn: Today.AddDays(-10));

        Assert.True(habit.ChangeFrequency(MonWedFri, Today, Now));

        Assert.Equal([(Today.AddDays(-10), HabitFrequencyKind.Daily), (Today, HabitFrequencyKind.Weekdays)], Versions(habit));
        Assert.Equal(HabitFrequencyKind.Daily, habit.VersionOn(Today.AddDays(-1))!.Kind);
        Assert.Equal(HabitFrequencyKind.Weekdays, habit.VersionOn(Today.AddDays(30))!.Kind);
        Assert.Null(habit.VersionOn(Today.AddDays(-11)));
    }

    [Fact]
    public void ChangeFrequency_TwiceOnTheSameDay_ReplacesTodaysVersion()
    {
        var habit = Create(startsOn: Today.AddDays(-10));

        habit.ChangeFrequency(MonWedFri, Today, Now);
        habit.ChangeFrequency(HabitFrequency.PerWeek(3), Today, Now);

        Assert.Equal([(Today.AddDays(-10), HabitFrequencyKind.Daily), (Today, HabitFrequencyKind.TimesPerWeek)], Versions(habit));
        Assert.Equal(3, habit.CurrentFrequency.TimesPerWeek);
    }

    [Fact]
    public void ChangeFrequency_BackToThePreviousOnTheSameDay_Merges()
    {
        var habit = Create(startsOn: Today.AddDays(-10));

        habit.ChangeFrequency(MonWedFri, Today, Now);
        habit.ChangeFrequency(HabitFrequency.Daily, Today, Now);

        Assert.Equal([(Today.AddDays(-10), HabitFrequencyKind.Daily)], Versions(habit));
    }

    [Fact]
    public void ChangeFrequency_ToTheSameFrequency_IsANoOp()
    {
        var habit = Create(startsOn: Today.AddDays(-10));
        var updated = habit.UpdatedAtUtc;

        Assert.False(habit.ChangeFrequency(HabitFrequency.Daily, Today, Now.AddHours(1)));

        Assert.Single(habit.Schedule);
        Assert.Equal(updated, habit.UpdatedAtUtc);
    }

    [Fact]
    public void ChangeFrequency_OnTheFirstDay_ReplacesTheFirstVersion()
    {
        var habit = Create();

        habit.ChangeFrequency(MonWedFri, Today, Now);

        Assert.Equal([(Today, HabitFrequencyKind.Weekdays)], Versions(habit));
    }

    [Fact]
    public void ChangeFrequency_BeforeTheHabitStarts_ChangesTheFirstVersion()
    {
        var habit = Create(startsOn: Today.AddDays(5));

        habit.ChangeFrequency(HabitFrequency.PerWeek(2), Today, Now);

        Assert.Equal([(Today.AddDays(5), HabitFrequencyKind.TimesPerWeek)], Versions(habit));
    }

    [Fact]
    public void ChangeFrequency_WhenTodayMovesBack_NeverRewritesALaterVersion()
    {
        var habit = Create(startsOn: Today.AddDays(-10));
        habit.ChangeFrequency(MonWedFri, Today, Now);

        // A device a time zone behind: its "today" is yesterday.
        habit.ChangeFrequency(HabitFrequency.PerWeek(2), Today.AddDays(-1), Now);

        Assert.Equal([(Today.AddDays(-10), HabitFrequencyKind.Daily), (Today, HabitFrequencyKind.TimesPerWeek)], Versions(habit));
    }

    [Fact]
    public void ChangeFrequency_IsRejected_WhileArchived_OrToInactive()
    {
        var habit = Create(startsOn: Today.AddDays(-10));

        Assert.Throws<ArgumentException>(() => habit.ChangeFrequency(HabitFrequency.Inactive, Today, Now));

        habit.Archive(Today, Now);
        var conflict = Assert.Throws<HabitConflictException>(() => habit.ChangeFrequency(MonWedFri, Today, Now));
        Assert.Equal("habit_archived", conflict.Code);
    }

    [Fact]
    public void UpdateDetails_ChangesNameAndDescription_AlsoWhileArchived()
    {
        var habit = Create();
        habit.Archive(Today, Now);

        habit.UpdateDetails(" Walk ", " Outside ", Now.AddHours(1));

        Assert.Equal(("Walk", "Outside"), (habit.Name, habit.Description));
        Assert.Equal(Now.AddHours(1), habit.UpdatedAtUtc);
        Assert.Throws<ArgumentException>(() => habit.UpdateDetails(" ", null, Now));
    }

    // ---- Archive / restore ----

    [Fact]
    public void Archive_AppendsInactiveFromToday_AndRestore_TheLastFrequencyFromToday()
    {
        var habit = Create(MonWedFri, Today.AddDays(-20));

        habit.Archive(Today.AddDays(-14), Now);
        Assert.Equal(HabitStatus.Archived, habit.Status);
        Assert.Equal(Now, habit.ArchivedAtUtc);
        Assert.Equal(HabitFrequency.Inactive, habit.CurrentFrequency);

        habit.Restore(Today, null, Now.AddDays(1));

        Assert.Equal((HabitStatus.Active, (DateTimeOffset?)null), (habit.Status, habit.ArchivedAtUtc));
        Assert.Equal(
            [
                (Today.AddDays(-20), HabitFrequencyKind.Weekdays),
                (Today.AddDays(-14), HabitFrequencyKind.Inactive),
                (Today, HabitFrequencyKind.Weekdays)
            ],
            Versions(habit));
        Assert.Equal(MonWedFri, habit.CurrentFrequency);
    }

    [Fact]
    public void ArchiveAndRestore_AreIdempotent()
    {
        var habit = Create(startsOn: Today.AddDays(-5));

        habit.Restore(Today, null, Now);
        Assert.Single(habit.Schedule);

        habit.Archive(Today, Now);
        habit.Archive(Today, Now.AddHours(2));
        Assert.Equal(Now, habit.ArchivedAtUtc);
        Assert.Equal(2, habit.Schedule.Count);
    }

    [Fact]
    public void ArchiveAndRestore_OnTheSameDay_LeaveTheScheduleAsItWas()
    {
        var habit = Create(MonWedFri, Today.AddDays(-5));

        habit.Archive(Today, Now);
        habit.Restore(Today, null, Now);

        Assert.Equal([(Today.AddDays(-5), HabitFrequencyKind.Weekdays)], Versions(habit));
    }

    [Fact]
    public void Restore_WithAnExplicitFrequency_AndAfterArchivingOnTheFirstDay()
    {
        var explicitly = Create(startsOn: Today.AddDays(-5));
        explicitly.Archive(Today.AddDays(-2), Now);
        explicitly.Restore(Today, HabitFrequency.PerWeek(4), Now);
        Assert.Equal(4, explicitly.CurrentFrequency.TimesPerWeek);

        // Archived on its first day: no earlier frequency remains, so it is restored as Daily.
        var firstDay = Create(MonWedFri);
        firstDay.Archive(Today, Now);
        Assert.Equal([(Today, HabitFrequencyKind.Inactive)], Versions(firstDay));
        firstDay.Restore(Today.AddDays(1), null, Now);
        Assert.Equal(HabitFrequency.Daily, firstDay.CurrentFrequency);
    }

    // ---- Check-in rules ----

    [Fact]
    public void CheckIn_TodayAndPastDays_TrimsTheNote()
    {
        var habit = Create(startsOn: Today.AddDays(-3));

        var done = habit.CheckIn(Today, HabitCheckInStatus.Done, null, Today, Now);
        var excused = habit.CheckIn(Today.AddDays(-3), HabitCheckInStatus.Excused, "  Sick  ", Today, Now);

        Assert.Equal((habit.Id, User, Today, HabitCheckInStatus.Done, (string?)null), (done.HabitId, done.UserId, done.Date, done.Status, done.Note));
        Assert.Equal((HabitCheckInStatus.Excused, "Sick"), (excused.Status, excused.Note));
        Assert.Null(habit.CheckIn(Today, HabitCheckInStatus.Excused, "  ", Today, Now).Note);
    }

    [Fact]
    public void CheckIn_RejectsFutureDates_AndDatesBeforeTheStart()
    {
        var habit = Create(startsOn: Today.AddDays(-3));

        Assert.Equal("date", Assert.Throws<ArgumentOutOfRangeException>(() =>
            habit.CheckIn(Today.AddDays(1), HabitCheckInStatus.Done, null, Today, Now)).ParamName);
        Assert.Equal("date", Assert.Throws<ArgumentOutOfRangeException>(() =>
            habit.CheckIn(Today.AddDays(-4), HabitCheckInStatus.Done, null, Today, Now)).ParamName);

        var upcoming = Create(startsOn: Today.AddDays(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => upcoming.CheckIn(Today, HabitCheckInStatus.Done, null, Today, Now));
    }

    [Fact]
    public void CheckIn_NoteIsBounded()
    {
        var habit = Create();

        Assert.Equal(HabitCheckIn.NoteMaxLength,
            habit.CheckIn(Today, HabitCheckInStatus.Excused, new string('n', HabitCheckIn.NoteMaxLength), Today, Now).Note!.Length);
        Assert.Equal("note", Assert.Throws<ArgumentException>(() =>
            habit.CheckIn(Today, HabitCheckInStatus.Excused, new string('n', HabitCheckIn.NoteMaxLength + 1), Today, Now)).ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => habit.CheckIn(Today, (HabitCheckInStatus)7, null, Today, Now));
    }

    [Fact]
    public void CheckIn_OnAnInactiveDate_IsAConflict()
    {
        var habit = Create(startsOn: Today.AddDays(-10));
        habit.Archive(Today.AddDays(-5), Now);
        habit.Restore(Today.AddDays(-2), null, Now);

        var conflict = Assert.Throws<HabitConflictException>(() =>
            habit.CheckIn(Today.AddDays(-4), HabitCheckInStatus.Done, null, Today, Now));
        Assert.Equal("habit_inactive_on_date", conflict.Code);

        // Before and after the archived period are fine; an archived habit's active past too.
        habit.CheckIn(Today.AddDays(-6), HabitCheckInStatus.Done, null, Today, Now);
        habit.CheckIn(Today.AddDays(-2), HabitCheckInStatus.Done, null, Today, Now);
        habit.Archive(Today, Now);
        habit.CheckIn(Today.AddDays(-1), HabitCheckInStatus.Done, null, Today, Now);
        Assert.Throws<HabitConflictException>(() => habit.CheckIn(Today, HabitCheckInStatus.Done, null, Today, Now));
    }

    [Fact]
    public void Excused_IsRejectedOnANonScheduledDay_ButDoneIsABonus()
    {
        // Wednesday 14 Oct; Tuesday 13 is not a Mon/Wed/Fri day.
        var habit = Create(MonWedFri, Today.AddDays(-7));

        Assert.Equal("status", Assert.Throws<ArgumentException>(() =>
            habit.CheckIn(Today.AddDays(-1), HabitCheckInStatus.Excused, null, Today, Now)).ParamName);
        Assert.Equal(HabitCheckInStatus.Done, habit.CheckIn(Today.AddDays(-1), HabitCheckInStatus.Done, null, Today, Now).Status);
        Assert.Equal(HabitCheckInStatus.Excused, habit.CheckIn(Today, HabitCheckInStatus.Excused, null, Today, Now).Status);

        // Every active day of a TimesPerWeek habit can be excused.
        var weekly = Create(HabitFrequency.PerWeek(3), Today.AddDays(-7));
        Assert.Equal(HabitCheckInStatus.Excused, weekly.CheckIn(Today.AddDays(-1), HabitCheckInStatus.Excused, null, Today, Now).Status);
    }

    [Fact]
    public void Done_IsRejectedForAHydrationHabit_ButExcusedIsAllowed()
    {
        var habit = Create(source: HabitCompletionSource.Hydration);

        Assert.Equal("habit_completion_derived", Assert.Throws<HabitConflictException>(() =>
            habit.CheckIn(Today, HabitCheckInStatus.Done, null, Today, Now)).Code);
        Assert.Equal(HabitCheckInStatus.Excused, habit.CheckIn(Today, HabitCheckInStatus.Excused, null, Today, Now).Status);
    }
}
