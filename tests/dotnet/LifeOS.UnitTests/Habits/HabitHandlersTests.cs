using LifeOS.Application.Habits;
using LifeOS.Domain.Habits;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Habits;

// HAB-001 §7 use cases over the in-memory repository: validation, limits, local "today" from the UTC
// offset, idempotent check-ins, ownership, versioned edits, archive/restore/delete and the Today list.
public class HabitHandlersTests
{
    // 08:00 UTC = 10:00 in Rome (+120): local today is Wednesday 14 Oct 2026.
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 14);
    private const int Rome = 120;

    private static readonly Guid A = TestUsers.A;
    private static readonly Guid B = TestUsers.B;

    private static readonly HabitFrequencyInput Daily = new(HabitFrequencyKind.Daily, null, null);
    private static readonly HabitFrequencyInput MonWedFri = new(HabitFrequencyKind.Weekdays, [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday], null);

    private readonly InMemoryHabitRepository _repository = new();
    private readonly ManualTimeProvider _clock = new(Now);

    private Task<HabitResult<HabitDetail>> CreateAsync(Guid user, string name = "Read 20 pages", HabitFrequencyInput? frequency = null,
        DateOnly? startsOn = null, int offset = Rome, HabitCompletionSource source = HabitCompletionSource.Manual, string? description = null) =>
        new CreateHabitHandler(_repository, _clock).HandleAsync(user,
            new CreateHabitCommand(name, description, source, frequency ?? Daily, startsOn, offset), CancellationToken.None);

    private async Task<Habit> HabitAsync(Guid user, HabitFrequencyInput? frequency = null, DateOnly? startsOn = null, string name = "Read 20 pages")
    {
        var result = await CreateAsync(user, name, frequency, startsOn);
        Assert.Equal(HabitResultStatus.Ok, result.Status);

        // Creation order is by CreatedAtUtc: one second apart keeps it unambiguous.
        _clock.Advance(TimeSpan.FromSeconds(1));
        return result.Value!.Habit;
    }

    private Task<HabitResult<HabitDayResult>> CheckInAsync(Guid user, Guid habitId, DateOnly date,
        HabitCheckInStatus status = HabitCheckInStatus.Done, string? note = null, int offset = Rome) =>
        new SetHabitCheckInHandler(_repository, _clock).HandleAsync(user, habitId, new SetHabitCheckInCommand(date, status, note, offset), CancellationToken.None);

    private Task<HabitResult<HabitDayResult>> UndoAsync(Guid user, Guid habitId, DateOnly date) =>
        new RemoveHabitCheckInHandler(_repository, _clock).HandleAsync(user, habitId, date, Rome, CancellationToken.None);

    private Task<HabitResult<HabitsForDay>> ListAsync(Guid user, DateOnly? date = null) =>
        new GetHabitsForDayHandler(_repository, _clock).HandleAsync(user, date ?? Today, Rome, CancellationToken.None);

    private Task<HabitResult<HabitDetail>> UpdateAsync(Guid user, Guid id, string name, HabitFrequencyInput? frequency = null) =>
        new UpdateHabitHandler(_repository, _clock).HandleAsync(user, id, new UpdateHabitCommand(name, null, frequency, Rome), CancellationToken.None);

    private Task<HabitResult<HabitDetail>> DetailAsync(Guid user, Guid id, int window = 30) =>
        new GetHabitDetailHandler(_repository, _clock).HandleAsync(user, id, Rome, window, CancellationToken.None);

    private ArchiveHabitHandler Archiver => new(_repository, _clock);

    // ---- Create ----

    [Fact]
    public async Task Create_DefaultsToLocalToday_AndReturnsTheDetail()
    {
        // 23:30 UTC on the 13th is already the 14th in Rome.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 13, 23, 30, 0, TimeSpan.Zero);

        var result = await CreateAsync(A, "  Stretch ", MonWedFri, description: " Ten minutes ");

        Assert.Equal(HabitResultStatus.Ok, result.Status);
        var detail = result.Value!;
        Assert.Equal(("Stretch", "Ten minutes", Today), (detail.Habit.Name, detail.Habit.Description, detail.Habit.StartsOn));
        Assert.Equal((HabitDayStatus.Pending, 0, 0), (detail.Progress.TodayStatus, detail.Progress.CurrentStreak, detail.Progress.BestStreak));
        Assert.Equal(new HabitAdherence(0, 0, null, 30), detail.Progress.Adherence);
        Assert.Equal(new DateOnly(2026, 9, 14), detail.RecentFrom);
        Assert.Equal(Today, detail.RecentDays[^1].Date);
        Assert.Single(_repository.Habits);
    }

    [Theory]
    [InlineData("", "name")]
    [InlineData("   ", "name")]
    public async Task Create_InvalidName_IsInvalid(string name, string field)
    {
        var result = await CreateAsync(A, name);

        Assert.Equal((HabitResultStatus.Invalid, field), (result.Status, result.Field));
        Assert.Empty(_repository.Habits);
    }

    [Fact]
    public async Task Create_InvalidInput_NamesTheField_WithAReadableMessage()
    {
        var noDays = await CreateAsync(A, frequency: new HabitFrequencyInput(HabitFrequencyKind.Weekdays, [], null));
        Assert.Equal(("frequency.weekdays", "Choose at least one day of the week."), (noDays.Field, noDays.Message));

        Assert.Equal("frequency.timesPerWeek", (await CreateAsync(A, frequency: new HabitFrequencyInput(HabitFrequencyKind.TimesPerWeek, null, 9))).Field);
        Assert.Equal("frequency.timesPerWeek", (await CreateAsync(A, frequency: new HabitFrequencyInput(HabitFrequencyKind.TimesPerWeek, null, null))).Field);
        Assert.Equal("frequency.kind", (await CreateAsync(A, frequency: new HabitFrequencyInput(HabitFrequencyKind.Inactive, null, null))).Field);
        Assert.Equal("startsOn", (await CreateAsync(A, startsOn: Today.AddDays(400))).Field);
        Assert.Equal("utcOffsetMinutes", (await CreateAsync(A, offset: 841)).Field);
        Assert.Equal("description", (await CreateAsync(A, description: new string('d', 501))).Field);

        // v1 creates manual habits only.
        var hydration = await CreateAsync(A, source: HabitCompletionSource.Hydration);
        Assert.Equal((HabitResultStatus.Invalid, "completionSource"), (hydration.Status, hydration.Field));

        Assert.Empty(_repository.Habits);
    }

    [Fact]
    public async Task Create_IsLimitedTo100ActiveHabits_ArchivedOnesDoNotCount()
    {
        for (var index = 0; index < HabitLimits.MaxActiveHabits; index++)
        {
            await HabitAsync(A, name: $"Habit {index}");
        }

        var limited = await CreateAsync(A);
        Assert.Equal((HabitResultStatus.Conflict, "habit_limit_reached"), (limited.Status, limited.Code));

        // Another user is unaffected; archiving one frees a slot.
        Assert.Equal(HabitResultStatus.Ok, (await CreateAsync(B)).Status);
        await Archiver.ArchiveAsync(A, _repository.Habits[0].Id, Rome, CancellationToken.None);
        Assert.Equal(HabitResultStatus.Ok, (await CreateAsync(A)).Status);
    }

    // ---- Check-ins ----

    [Fact]
    public async Task CheckIn_IsAnIdempotentUpsert_AndUndoIsIdempotent()
    {
        var habit = await HabitAsync(A, startsOn: Today.AddDays(-3));

        var done = await CheckInAsync(A, habit.Id, Today);
        Assert.Equal(HabitResultStatus.Ok, done.Status);
        Assert.Equal((HabitDayStatus.Done, 1), (done.Value!.Day.Status, done.Value.Progress.CurrentStreak));

        await CheckInAsync(A, habit.Id, Today);
        Assert.Single(_repository.CheckIns);

        var excused = await CheckInAsync(A, habit.Id, Today, HabitCheckInStatus.Excused, " Sick ");
        Assert.Equal((HabitDayStatus.Excused, "Sick"), (excused.Value!.Day.Status, excused.Value.Day.Note));
        Assert.Single(_repository.CheckIns);

        var undone = await UndoAsync(A, habit.Id, Today);
        Assert.Equal((HabitResultStatus.Ok, HabitDayStatus.Pending), (undone.Status, undone.Value!.Day.Status));
        Assert.Empty(_repository.CheckIns);

        var again = await UndoAsync(A, habit.Id, Today);
        Assert.Equal(HabitResultStatus.Ok, again.Status);

        // A past day returns to Missed.
        await CheckInAsync(A, habit.Id, Today.AddDays(-1));
        Assert.Equal(HabitDayStatus.Missed, (await UndoAsync(A, habit.Id, Today.AddDays(-1))).Value!.Day.Status);
    }

    [Fact]
    public async Task CheckIn_TodayFollowsTheOffset()
    {
        _clock.UtcNow = new DateTimeOffset(2026, 10, 13, 23, 30, 0, TimeSpan.Zero);
        var habit = await HabitAsync(A, startsOn: Today.AddDays(-5));

        // In UTC it is still the 13th: the 14th is a future date.
        var utc = await CheckInAsync(A, habit.Id, Today, offset: 0);
        Assert.Equal((HabitResultStatus.Invalid, "date"), (utc.Status, utc.Field));

        Assert.Equal(HabitResultStatus.Ok, (await CheckInAsync(A, habit.Id, Today, offset: 60)).Status);
        Assert.Equal("utcOffsetMinutes", (await CheckInAsync(A, habit.Id, Today, offset: -900)).Field);
    }

    [Fact]
    public async Task CheckIn_Rules_MapToInvalidAndConflict()
    {
        var habit = await HabitAsync(A, MonWedFri, Today.AddDays(-9));

        Assert.Equal((HabitResultStatus.Invalid, "date"), Pair(await CheckInAsync(A, habit.Id, Today.AddDays(-10))));
        Assert.Equal((HabitResultStatus.Invalid, "status"), Pair(await CheckInAsync(A, habit.Id, Today.AddDays(-1), HabitCheckInStatus.Excused)));
        Assert.Equal((HabitResultStatus.Invalid, "note"), Pair(await CheckInAsync(A, habit.Id, Today, HabitCheckInStatus.Excused, new string('n', 281))));

        await Archiver.ArchiveAsync(A, habit.Id, Rome, CancellationToken.None);
        var archived = await CheckInAsync(A, habit.Id, Today);
        Assert.Equal((HabitResultStatus.Conflict, "habit_inactive_on_date"), (archived.Status, archived.Code));

        // The active past of an archived habit can still be corrected.
        Assert.Equal(HabitResultStatus.Ok, (await CheckInAsync(A, habit.Id, Today.AddDays(-2))).Status);
        Assert.Equal((HabitResultStatus.Invalid, "date"), Pair(await UndoAsync(A, habit.Id, Today.AddDays(1))));

        static (HabitResultStatus, string?) Pair(HabitResult<HabitDayResult> result) => (result.Status, result.Field);
    }

    // ---- Ownership ----

    [Fact]
    public async Task AnotherUsersHabit_IsNotFound_AndStaysUntouched()
    {
        var habit = await HabitAsync(A, startsOn: Today.AddDays(-3));
        await CheckInAsync(A, habit.Id, Today);

        Assert.Equal(HabitResultStatus.NotFound, (await DetailAsync(B, habit.Id)).Status);
        Assert.Equal(HabitResultStatus.NotFound, (await UpdateAsync(B, habit.Id, "Stolen")).Status);
        Assert.Equal(HabitResultStatus.NotFound, (await CheckInAsync(B, habit.Id, Today.AddDays(-1))).Status);
        Assert.Equal(HabitResultStatus.NotFound, (await UndoAsync(B, habit.Id, Today)).Status);
        Assert.Equal(HabitResultStatus.NotFound, (await Archiver.ArchiveAsync(B, habit.Id, Rome, CancellationToken.None)).Status);
        Assert.Equal(HabitResultStatus.NotFound, (await Archiver.RestoreAsync(B, habit.Id, Rome, CancellationToken.None)).Status);
        Assert.Equal(HabitResultStatus.NotFound,
            (await new GetHabitHistoryHandler(_repository, _clock).HandleAsync(B, habit.Id, Today.AddDays(-3), Today, Rome, CancellationToken.None)).Status);
        Assert.Equal(HabitResultStatus.NotFound, await new DeleteHabitHandler(_repository).HandleAsync(B, habit.Id, CancellationToken.None));
        Assert.Empty((await ListAsync(B)).Value!.Items);

        var stored = (await DetailAsync(A, habit.Id)).Value!;
        Assert.Equal(("Read 20 pages", HabitDayStatus.Done), (stored.Habit.Name, stored.Progress.TodayStatus));
        Assert.Single(_repository.CheckIns);
    }

    // ---- Edit / archive / restore / delete ----

    [Fact]
    public async Task Edit_FrequencyAppliesFromToday_AndPastDaysKeepTheirSchedule()
    {
        var habit = await HabitAsync(A, startsOn: Today.AddDays(-7));

        var updated = await UpdateAsync(A, habit.Id, " Read ", MonWedFri);

        Assert.Equal(HabitResultStatus.Ok, updated.Status);
        var detail = updated.Value!;
        Assert.Equal("Read", detail.Habit.Name);
        Assert.Equal([HabitFrequencyKind.Daily, HabitFrequencyKind.Weekdays], detail.Habit.Schedule.Select(version => version.Kind));
        // Tuesday 13 Oct stays a Missed Daily day; Thursday 15 is not scheduled any more.
        Assert.Equal(HabitDayStatus.Missed, detail.RecentDays.Single(day => day.Date == Today.AddDays(-1)).Status);

        // Name only: the frequency is unchanged.
        var renamed = await UpdateAsync(A, habit.Id, "Read more");
        Assert.Equal(2, renamed.Value!.Habit.Schedule.Count);

        Assert.Equal((HabitResultStatus.Invalid, "name"), ((await UpdateAsync(A, habit.Id, " ")).Status, (await UpdateAsync(A, habit.Id, " ")).Field));
    }

    [Fact]
    public async Task Archive_HidesFromToday_Restore_SchedulesAgain_FrequencyChangeWhileArchivedIsAConflict()
    {
        var habit = await HabitAsync(A, MonWedFri, Today.AddDays(-7));

        var archived = await Archiver.ArchiveAsync(A, habit.Id, Rome, CancellationToken.None);
        Assert.Equal((HabitStatus.Archived, HabitDayStatus.Inactive), (archived.Value!.Habit.Status, archived.Value.Progress.TodayStatus));

        var list = (await ListAsync(A)).Value!;
        Assert.Empty(list.Items);
        Assert.Equal(1, list.ArchivedCount);
        Assert.Single(await new GetArchivedHabitsHandler(_repository).HandleAsync(A, CancellationToken.None));

        var conflict = await UpdateAsync(A, habit.Id, "Read", Daily);
        Assert.Equal((HabitResultStatus.Conflict, "habit_archived"), (conflict.Status, conflict.Code));
        Assert.Equal("Read 20 pages", (await DetailAsync(A, habit.Id)).Value!.Habit.Name);

        // Renaming alone is allowed while archived.
        Assert.Equal(HabitResultStatus.Ok, (await UpdateAsync(A, habit.Id, "Read")).Status);

        _clock.Advance(TimeSpan.FromDays(2));
        var restored = await Archiver.RestoreAsync(A, habit.Id, Rome, CancellationToken.None);
        Assert.Equal(HabitStatus.Active, restored.Value!.Habit.Status);
        Assert.Equal(
            [HabitFrequencyKind.Weekdays, HabitFrequencyKind.Inactive, HabitFrequencyKind.Weekdays],
            restored.Value.Habit.Schedule.Select(version => version.Kind));
        Assert.Single((await new GetHabitsForDayHandler(_repository, _clock).HandleAsync(A, Today.AddDays(2), Rome, CancellationToken.None)).Value!.Items);
    }

    [Fact]
    public async Task Delete_RemovesTheHabitAndItsCheckIns()
    {
        var habit = await HabitAsync(A, startsOn: Today.AddDays(-3));
        var other = await HabitAsync(A, startsOn: Today.AddDays(-3), name: "Walk");
        await CheckInAsync(A, habit.Id, Today);
        await CheckInAsync(A, other.Id, Today);

        Assert.Equal(HabitResultStatus.Ok, await new DeleteHabitHandler(_repository).HandleAsync(A, habit.Id, CancellationToken.None));

        Assert.Equal(HabitResultStatus.NotFound, (await DetailAsync(A, habit.Id)).Status);
        Assert.Equal(other.Id, Assert.Single(_repository.CheckIns.Values).HabitId);
        Assert.Equal(HabitResultStatus.NotFound, await new DeleteHabitHandler(_repository).HandleAsync(A, habit.Id, CancellationToken.None));
    }

    // ---- Today list ----

    [Fact]
    public async Task List_GroupsToDoDoneAndNotToday_WithUpcomingAndArchivedCounts()
    {
        var daily = await HabitAsync(A, startsOn: Today.AddDays(-7), name: "Daily");
        var done = await HabitAsync(A, startsOn: Today.AddDays(-7), name: "Done");
        var tuesdays = await HabitAsync(A, new HabitFrequencyInput(HabitFrequencyKind.Weekdays, [DayOfWeek.Tuesday], null), Today.AddDays(-7), "Tuesdays");
        var weekly = await HabitAsync(A, new HabitFrequencyInput(HabitFrequencyKind.TimesPerWeek, null, 2), Today.AddDays(-7), "Weekly");
        var upcoming = await HabitAsync(A, startsOn: Today.AddDays(3), name: "Later");
        var archived = await HabitAsync(A, startsOn: Today.AddDays(-7), name: "Old");
        await Archiver.ArchiveAsync(A, archived.Id, Rome, CancellationToken.None);
        await CheckInAsync(A, done.Id, Today);
        await CheckInAsync(A, weekly.Id, Today.AddDays(-2));

        var list = (await ListAsync(A)).Value!;

        Assert.Equal(["Daily", "Done", "Tuesdays", "Weekly"], list.Items.Select(item => item.Habit.Name));
        Assert.Equal(
            [
                (HabitDayStatus.Pending, HabitListGroup.ToDo),
                (HabitDayStatus.Done, HabitListGroup.Done),
                (HabitDayStatus.NotScheduled, HabitListGroup.NotToday),
                (HabitDayStatus.Pending, HabitListGroup.ToDo)
            ],
            list.Items.Select(item => (item.Status, item.Group)));
        Assert.Equal(new HabitWeekProgress(new DateOnly(2026, 10, 12), 1, 2), list.Items[3].Week);
        Assert.Equal((HabitStreakUnit.Week, 0), (list.Items[3].StreakUnit, list.Items[3].CurrentStreak));
        Assert.Equal(upcoming.Id, Assert.Single(list.Upcoming).Id);
        Assert.Equal(1, list.ArchivedCount);

        // Week met: listed as done this week.
        await CheckInAsync(A, weekly.Id, Today.AddDays(-1));
        Assert.Equal(HabitListGroup.Done, (await ListAsync(A)).Value!.Items.Single(item => item.Habit.Id == weekly.Id).Group);
        Assert.NotNull(daily);
        Assert.NotNull(tuesdays);
    }

    [Fact]
    public async Task List_ForAPastDay_ShowsThatDaysStatus_AndAWeeklyHabitStaysToDoUntilMet()
    {
        var daily = await HabitAsync(A, startsOn: Today.AddDays(-7), name: "Daily");
        var weekly = await HabitAsync(A, new HabitFrequencyInput(HabitFrequencyKind.TimesPerWeek, null, 3), Today.AddDays(-7), "Weekly");
        await CheckInAsync(A, daily.Id, Today.AddDays(-2), HabitCheckInStatus.Excused, "Travel");

        var monday = (await ListAsync(A, Today.AddDays(-2))).Value!;

        Assert.Equal((HabitDayStatus.Excused, "Travel", HabitListGroup.Done), (monday.Items[0].Status, monday.Items[0].Note, monday.Items[0].Group));
        Assert.Equal((HabitDayStatus.NotScheduled, HabitListGroup.ToDo), (monday.Items[1].Status, monday.Items[1].Group));
        Assert.Equal(HabitDayStatus.Missed, (await ListAsync(A, Today.AddDays(-1))).Value!.Items[0].Status);
        Assert.NotNull(weekly);

        // A future date is rejected; a habit starting after the viewed date is upcoming there.
        Assert.Equal((HabitResultStatus.Invalid, "date"), ((await ListAsync(A, Today.AddDays(1))).Status, (await ListAsync(A, Today.AddDays(1))).Field));
        Assert.Equal(2, (await ListAsync(A, Today.AddDays(-8))).Value!.Upcoming.Count);
    }

    [Fact]
    public async Task List_LongStreak_ReadsTheFullHistoryOnce()
    {
        var habit = await HabitAsync(A, startsOn: Today.AddDays(-100));

        for (var date = Today.AddDays(-100); date < Today; date = date.AddDays(1))
        {
            await CheckInAsync(A, habit.Id, date);
        }

        _repository.CheckInReads.Clear();
        var item = Assert.Single((await ListAsync(A)).Value!.Items);

        Assert.Equal(100, item.CurrentStreak);
        Assert.Equal(2, _repository.CheckInReads.Count);
        Assert.Equal(Today.AddDays(-100), _repository.CheckInReads[1].From);
    }

    // ---- Detail / history ----

    [Fact]
    public async Task Detail_WindowIs7_30Or90_AndBestStreakUsesTheWholeHistory()
    {
        var habit = await HabitAsync(A, startsOn: Today.AddDays(-20));

        foreach (var date in new[] { -20, -19, -18, -17, -16, -10, -2, -1 }.Select(Today.AddDays))
        {
            await CheckInAsync(A, habit.Id, date);
        }

        var detail = (await DetailAsync(A, habit.Id, 7)).Value!;
        Assert.Equal((2, 5), (detail.Progress.CurrentStreak, detail.Progress.BestStreak));
        Assert.Equal(new HabitAdherence(2, 6, 33, 7), detail.Progress.Adherence);
        Assert.Equal(new DateOnly(2026, 9, 14), detail.RecentFrom);
        Assert.Equal(31, detail.RecentDays.Count);

        Assert.Equal(90, (await DetailAsync(A, habit.Id, 90)).Value!.Progress.Adherence.WindowDays);
        Assert.Equal((HabitResultStatus.Invalid, "window"), ((await DetailAsync(A, habit.Id, 14)).Status, (await DetailAsync(A, habit.Id, 14)).Field));
    }

    [Fact]
    public async Task History_IsBoundedTo366DaysUpToToday()
    {
        var habit = await HabitAsync(A, startsOn: Today.AddDays(-3));
        await CheckInAsync(A, habit.Id, Today.AddDays(-1), HabitCheckInStatus.Excused, "Rest");
        var handler = new GetHabitHistoryHandler(_repository, _clock);

        var history = (await handler.HandleAsync(A, habit.Id, Today.AddDays(-5), Today, Rome, CancellationToken.None)).Value!;
        Assert.Equal(
            [
                HabitDayStatus.Inactive, HabitDayStatus.Inactive, HabitDayStatus.Missed, HabitDayStatus.Missed, HabitDayStatus.Excused, HabitDayStatus.Pending
            ],
            history.Days.Select(day => day.Status));
        Assert.Equal("Rest", history.Days[4].Note);

        Assert.Equal(HabitResultStatus.Ok, (await handler.HandleAsync(A, habit.Id, Today.AddDays(-365), Today, Rome, CancellationToken.None)).Status);
        Assert.Equal("from", (await handler.HandleAsync(A, habit.Id, Today.AddDays(-366), Today, Rome, CancellationToken.None)).Field);
        Assert.Equal("to", (await handler.HandleAsync(A, habit.Id, Today, Today.AddDays(1), Rome, CancellationToken.None)).Field);
        Assert.Equal("from", (await handler.HandleAsync(A, habit.Id, Today, Today.AddDays(-1), Rome, CancellationToken.None)).Field);
    }

    [Theory]
    [InlineData(HabitDayStatus.Done, false, HabitListGroup.Done)]
    [InlineData(HabitDayStatus.Excused, false, HabitListGroup.Done)]
    [InlineData(HabitDayStatus.Pending, false, HabitListGroup.ToDo)]
    [InlineData(HabitDayStatus.Missed, false, HabitListGroup.ToDo)]
    [InlineData(HabitDayStatus.NotScheduled, false, HabitListGroup.NotToday)]
    [InlineData(HabitDayStatus.Inactive, false, HabitListGroup.NotToday)]
    [InlineData(HabitDayStatus.NotScheduled, true, HabitListGroup.ToDo)]
    [InlineData(HabitDayStatus.Pending, true, HabitListGroup.ToDo)]
    [InlineData(HabitDayStatus.Inactive, true, HabitListGroup.NotToday)]
    public void Group_FollowsTheDayAndTheWeek(HabitDayStatus status, bool weekOpen, HabitListGroup expected) =>
        Assert.Equal(expected, GetHabitsForDayHandler.Group(status, weekOpen ? new HabitWeekProgress(Today, 1, 3) : null));

    [Fact]
    public void Group_AMetOrFullyExcusedWeek()
    {
        Assert.Equal(HabitListGroup.Done, GetHabitsForDayHandler.Group(HabitDayStatus.Pending, new HabitWeekProgress(Today, 3, 3)));
        Assert.Equal(HabitListGroup.NotToday, GetHabitsForDayHandler.Group(HabitDayStatus.NotScheduled, new HabitWeekProgress(Today, 0, 0)));
    }
}
