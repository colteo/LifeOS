using LifeOS.Application.Reminders;
using LifeOS.Domain.Reminders;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Reminders;

// REM-001 §8 use cases over the in-memory repository: validation, limits, local "now" from the UTC
// offset, ownership, pause/resume/snooze conflicts, list grouping and the quiet-hours flag.
public class ReminderHandlersTests
{
    // 07:00 UTC = 09:00 in Rome (+120): Wednesday 14 Oct 2026.
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 7, 0, 0, TimeSpan.Zero);
    private const int Rome = 120;

    private static readonly Guid A = TestUsers.A;
    private static readonly Guid B = TestUsers.B;

    private static readonly ReminderScheduleInput Daily0830 = new(ReminderScheduleKind.Daily, null, null, new TimeOnly(8, 30));
    private static readonly ReminderScheduleInput Dentist = new(ReminderScheduleKind.Once, new DateOnly(2026, 10, 20), null, new TimeOnly(10, 0));

    private readonly InMemoryReminderRepository _repository = new();
    private readonly InMemoryNotificationPreferencesRepository _preferences = new();
    private readonly ManualTimeProvider _clock = new(Now);

    private Task<ReminderResult<ReminderView>> CreateAsync(Guid user, ReminderScheduleInput? schedule = null, string title = "Take vitamins",
        int offset = Rome, string? note = null) =>
        new CreateReminderHandler(_repository, _preferences, _clock).HandleAsync(user,
            new CreateReminderCommand(title, note, schedule ?? Daily0830, offset), CancellationToken.None);

    private async Task<Reminder> ReminderAsync(Guid user, ReminderScheduleInput? schedule = null, string title = "Take vitamins")
    {
        var result = await CreateAsync(user, schedule, title);
        Assert.Equal(ReminderResultStatus.Ok, result.Status);

        _clock.Advance(TimeSpan.FromSeconds(1));
        return result.Value!.Reminder;
    }

    private PauseReminderHandler Pause => new(_repository, _preferences, _clock);

    private SnoozeReminderHandler Snooze => new(_repository, _preferences, _clock);

    // ---- Create ----

    [Fact]
    public async Task Create_ComputesTheNextOccurrenceFromTheDevicesLocalTime()
    {
        var rome = await CreateAsync(A);
        Assert.Equal(new DateTime(2026, 10, 15, 8, 30, 0), rome.Value!.Reminder.NextDueLocal);

        // 07:00Z is 03:00 in New York (-240): 08:30 is still ahead today.
        var newYork = await CreateAsync(A, offset: -240);
        Assert.Equal(new DateTime(2026, 10, 14, 8, 30, 0), newYork.Value!.Reminder.NextDueLocal);

        Assert.Equal(2, _repository.Reminders.Count);
    }

    [Fact]
    public async Task Create_InvalidInput_IsFieldKeyed()
    {
        Assert.Equal("title", (await CreateAsync(A, title: " ")).Field);
        Assert.Equal("note", (await CreateAsync(A, note: new string('n', 501))).Field);
        Assert.Equal("utcOffsetMinutes", (await CreateAsync(A, offset: 841)).Field);
        Assert.Equal("schedule.date", (await CreateAsync(A, new ReminderScheduleInput(ReminderScheduleKind.Once, null, null, new TimeOnly(10, 0)))).Field);
        Assert.Equal("schedule.weekdays", (await CreateAsync(A, new ReminderScheduleInput(ReminderScheduleKind.Weekly, null, [], new TimeOnly(10, 0)))).Field);
        Assert.Equal("schedule.time", (await CreateAsync(A, new ReminderScheduleInput(ReminderScheduleKind.Daily, null, null, new TimeOnly(10, 0, 30)))).Field);

        var past = await CreateAsync(A, new ReminderScheduleInput(ReminderScheduleKind.Once, new DateOnly(2026, 10, 14), null, new TimeOnly(8, 59)));
        Assert.Equal((ReminderResultStatus.Invalid, "schedule.date", "Choose a date and time in the future."), (past.Status, past.Field, past.Message));

        Assert.Empty(_repository.Reminders);
    }

    [Fact]
    public async Task Create_AtTheLimit_IsAConflict_CompletedOnesDoNotCount()
    {
        for (var index = 0; index < ReminderLimits.MaxOpenReminders; index++)
        {
            await CreateAsync(A);
        }

        var refused = await CreateAsync(A);
        Assert.Equal((ReminderResultStatus.Conflict, ReminderLimits.LimitReachedCode), (refused.Status, refused.Code));

        // Another user is not affected; a completed reminder frees a place.
        Assert.Equal(ReminderResultStatus.Ok, (await CreateAsync(B)).Status);

        var first = _repository.Reminders.First(reminder => reminder.UserId == A);
        first.Update("Done", null, ReminderSchedule.Once(new DateOnly(2026, 10, 20), new TimeOnly(10, 0)), new DateTime(2026, 10, 14, 9, 0, 0), Now);
        first.Advance(new DateTime(2026, 10, 20, 10, 0, 0), ReminderOutcome.Notified, Now);

        Assert.Equal(ReminderResultStatus.Ok, (await CreateAsync(A)).Status);
    }

    [Fact]
    public async Task Create_FlagsATimeInsideQuietHours()
    {
        _preferences.Set(A, start: new TimeOnly(22, 0), end: new TimeOnly(8, 0));

        Assert.False((await CreateAsync(A)).Value!.InsideQuietHours);
        Assert.True((await CreateAsync(A, new ReminderScheduleInput(ReminderScheduleKind.Daily, null, null, new TimeOnly(23, 0)))).Value!.InsideQuietHours);
        Assert.True((await CreateAsync(A, new ReminderScheduleInput(ReminderScheduleKind.Daily, null, null, new TimeOnly(7, 59)))).Value!.InsideQuietHours);
        Assert.False((await CreateAsync(A, new ReminderScheduleInput(ReminderScheduleKind.Daily, null, null, new TimeOnly(8, 0)))).Value!.InsideQuietHours);
    }

    // ---- Update ----

    [Fact]
    public async Task Update_RecomputesFromNow_AndIsOwnerScoped()
    {
        var reminder = await ReminderAsync(A);
        var handler = new UpdateReminderHandler(_repository, _preferences, _clock);
        var weekly = new ReminderScheduleInput(ReminderScheduleKind.Weekly, null, [DayOfWeek.Friday], new TimeOnly(19, 0));

        Assert.Equal(ReminderResultStatus.NotFound,
            (await handler.HandleAsync(B, reminder.Id, new UpdateReminderCommand("x", null, weekly, Rome), CancellationToken.None)).Status);

        var updated = await handler.HandleAsync(A, reminder.Id, new UpdateReminderCommand("Water the plants", "Balcony", weekly, Rome), CancellationToken.None);

        Assert.Equal(ReminderResultStatus.Ok, updated.Status);
        var stored = _repository.Find(A, reminder.Id)!;
        Assert.Equal(("Water the plants", "Balcony", (DateTime?)new DateTime(2026, 10, 16, 19, 0, 0)), (stored.Title, stored.Note, stored.NextDueLocal));
    }

    [Fact]
    public async Task Update_ReopeningACompletedReminder_RespectsTheLimit()
    {
        var once = await ReminderAsync(A, Dentist);
        _repository.Find(A, once.Id)!.Advance(new DateTime(2026, 10, 20, 10, 0, 0), ReminderOutcome.Notified, Now);

        for (var index = 0; index < ReminderLimits.MaxOpenReminders; index++)
        {
            await CreateAsync(A);
        }

        var handler = new UpdateReminderHandler(_repository, _preferences, _clock);
        var result = await handler.HandleAsync(A, once.Id, new UpdateReminderCommand("Call again", null, Daily0830, Rome), CancellationToken.None);

        Assert.Equal((ReminderResultStatus.Conflict, ReminderLimits.LimitReachedCode), (result.Status, result.Code));
        Assert.Equal(ReminderStatus.Completed, _repository.Find(A, once.Id)!.Status);
    }

    [Fact]
    public async Task Update_Invalid_LeavesTheStoredReminderUnchanged()
    {
        var reminder = await ReminderAsync(A);
        var handler = new UpdateReminderHandler(_repository, _preferences, _clock);

        var result = await handler.HandleAsync(A, reminder.Id, new UpdateReminderCommand("", null, Daily0830, Rome), CancellationToken.None);

        Assert.Equal((ReminderResultStatus.Invalid, "title"), (result.Status, result.Field));
        Assert.Equal("Take vitamins", _repository.Find(A, reminder.Id)!.Title);
    }

    // ---- Pause / resume / snooze ----

    [Fact]
    public async Task PauseResume_NoCatchUp_CompletedIsAConflict()
    {
        var reminder = await ReminderAsync(A);

        Assert.Equal(ReminderStatus.Paused, (await Pause.PauseAsync(A, reminder.Id, CancellationToken.None)).Value!.Reminder.Status);
        Assert.Equal(ReminderResultStatus.NotFound, (await Pause.PauseAsync(B, reminder.Id, CancellationToken.None)).Status);

        _clock.Advance(TimeSpan.FromDays(3));
        var resumed = await Pause.ResumeAsync(A, reminder.Id, Rome, CancellationToken.None);
        Assert.Equal((ReminderStatus.Active, (DateTime?)new DateTime(2026, 10, 18, 8, 30, 0)), (resumed.Value!.Reminder.Status, resumed.Value.Reminder.NextDueLocal));

        Assert.Equal("utcOffsetMinutes", (await Pause.ResumeAsync(A, reminder.Id, -900, CancellationToken.None)).Field);

        var once = await ReminderAsync(A, Dentist);
        _repository.Find(A, once.Id)!.Advance(new DateTime(2026, 10, 20, 10, 0, 0), ReminderOutcome.Notified, Now);

        var conflict = await Pause.ResumeAsync(A, once.Id, Rome, CancellationToken.None);
        Assert.Equal((ReminderResultStatus.Conflict, ReminderConflictException.Completed), (conflict.Status, conflict.Code));
        Assert.Equal(ReminderConflictException.Completed, (await Pause.PauseAsync(A, once.Id, CancellationToken.None)).Code);
    }

    [Fact]
    public async Task Snooze_ValidatesMinutes_AndStates()
    {
        var reminder = await ReminderAsync(A);

        Assert.Equal((ReminderResultStatus.Invalid, "minutes"), Shape(await Snooze.HandleAsync(A, reminder.Id, 30, CancellationToken.None)));
        Assert.Equal(ReminderResultStatus.NotFound, (await Snooze.HandleAsync(B, reminder.Id, 15, CancellationToken.None)).Status);

        var snoozed = await Snooze.HandleAsync(A, reminder.Id, 60, CancellationToken.None);
        Assert.Equal(Now.AddMinutes(60), snoozed.Value!.Reminder.SnoozeUntilUtc);

        await Pause.PauseAsync(A, reminder.Id, CancellationToken.None);
        var conflict = await Snooze.HandleAsync(A, reminder.Id, 15, CancellationToken.None);
        Assert.Equal((ReminderResultStatus.Conflict, ReminderConflictException.Paused), (conflict.Status, conflict.Code));
    }

    // ---- Delete / read ----

    [Fact]
    public async Task Delete_And_Get_AreOwnerScoped()
    {
        var reminder = await ReminderAsync(A);
        var delete = new DeleteReminderHandler(_repository);
        var get = new GetReminderHandler(_repository, _preferences);

        Assert.Equal(ReminderResultStatus.NotFound, (await get.HandleAsync(B, reminder.Id, CancellationToken.None)).Status);
        Assert.Equal(ReminderResultStatus.NotFound, await delete.HandleAsync(B, reminder.Id, CancellationToken.None));
        Assert.Equal(ReminderResultStatus.Ok, (await get.HandleAsync(A, reminder.Id, CancellationToken.None)).Status);

        Assert.Equal(ReminderResultStatus.Ok, await delete.HandleAsync(A, reminder.Id, CancellationToken.None));
        Assert.Equal(ReminderResultStatus.NotFound, await delete.HandleAsync(A, reminder.Id, CancellationToken.None));
        Assert.Equal(ReminderResultStatus.NotFound, (await get.HandleAsync(A, reminder.Id, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task List_GroupsAndOrders_AndReportsZoneAndPush()
    {
        var later = await ReminderAsync(A, new ReminderScheduleInput(ReminderScheduleKind.Daily, null, null, new TimeOnly(23, 0)), "Later");
        var sooner = await ReminderAsync(A, new ReminderScheduleInput(ReminderScheduleKind.Daily, null, null, new TimeOnly(10, 0)), "Sooner");
        var paused = await ReminderAsync(A, title: "Paused");
        await Pause.PauseAsync(A, paused.Id, CancellationToken.None);
        var completed = await ReminderAsync(A, Dentist, "Completed");
        _repository.Find(A, completed.Id)!.Advance(new DateTime(2026, 10, 20, 10, 0, 0), ReminderOutcome.Notified, Now);
        var old = await ReminderAsync(A, Dentist, "Old");
        _repository.Find(A, old.Id)!.Advance(new DateTime(2026, 10, 20, 10, 0, 0), ReminderOutcome.Notified, Now.AddDays(-31));
        await ReminderAsync(B, title: "Other user");

        var withoutPush = await new GetRemindersHandler(_repository, _preferences, _clock).HandleAsync(A, CancellationToken.None);

        Assert.Equal(["Sooner", "Later"], withoutPush.Upcoming.Select(view => view.Reminder.Title));
        Assert.Equal(["Paused"], withoutPush.Paused.Select(view => view.Reminder.Title));
        Assert.Equal(["Completed"], withoutPush.Completed.Select(view => view.Reminder.Title));
        Assert.Equal((false, false), (withoutPush.TimeZoneKnown, withoutPush.PushAvailable));
        Assert.True(withoutPush.Upcoming.Single(view => view.Reminder.Id == later.Id).InsideQuietHours);
        Assert.False(withoutPush.Upcoming.Single(view => view.Reminder.Id == sooner.Id).InsideQuietHours);

        _preferences.TimeZones[A] = "Europe/Rome";
        var withPush = await new GetRemindersHandler(_repository, _preferences, _clock, new FakePushNotificationSender()).HandleAsync(A, CancellationToken.None);
        Assert.Equal((true, true), (withPush.TimeZoneKnown, withPush.PushAvailable));
    }

    [Fact]
    public async Task List_KeepsAnOldCompletedReminderWithAPendingSnooze()
    {
        var old = await ReminderAsync(A, Dentist, "Old");
        var stored = _repository.Find(A, old.Id)!;
        stored.Advance(new DateTime(2026, 10, 20, 10, 0, 0), ReminderOutcome.Notified, Now.AddDays(-40));
        stored.Snooze(180, Now.AddDays(-40));

        var overview = await new GetRemindersHandler(_repository, _preferences, _clock).HandleAsync(A, CancellationToken.None);

        Assert.Equal(["Old"], overview.Completed.Select(view => view.Reminder.Title));
    }

    // ---- Conditional reminders (R5) ----

    private sealed class FakeHabitStatus : LifeOS.Application.Habits.IHabitDayStatusReader
    {
        public HashSet<(Guid UserId, Guid HabitId)> Habits { get; } = [];

        public Task<LifeOS.Application.Habits.HabitDayStatusSnapshot?> GetStatusAsync(Guid userId, Guid habitId, DateOnly date,
            CancellationToken cancellationToken) =>
            Task.FromResult(Habits.Contains((userId, habitId))
                ? new LifeOS.Application.Habits.HabitDayStatusSnapshot(habitId, true, LifeOS.Domain.Habits.HabitDayStatus.Pending, null, null)
                : null);
    }

    private readonly FakeHabitStatus _habits = new();

    private Task<ReminderResult<ReminderView>> CreateConditionalAsync(Guid user, ReminderKind kind, Guid? habitId = null,
        ReminderScheduleInput? schedule = null) =>
        new CreateReminderHandler(_repository, _preferences, _clock, _habits).HandleAsync(user,
            new CreateReminderCommand(null, null, schedule ?? Daily0830, Rome, kind, habitId), CancellationToken.None);

    [Fact]
    public async Task CreateHabitReminder_NeedsTheUsersOwnHabit_AndOneReminderPerHabit()
    {
        var habit = Guid.CreateVersion7();
        _habits.Habits.Add((A, habit));
        _repository.HabitNames[(A, habit)] = "Read 20 pages";

        Assert.Equal(ReminderResultStatus.NotFound, (await CreateConditionalAsync(B, ReminderKind.HabitIncomplete, habit)).Status);
        Assert.Equal(ReminderResultStatus.NotFound, (await CreateConditionalAsync(A, ReminderKind.HabitIncomplete, Guid.CreateVersion7())).Status);
        Assert.Equal(("habitId"), (await CreateConditionalAsync(A, ReminderKind.HabitIncomplete)).Field);
        Assert.Equal(("schedule.kind"), (await CreateConditionalAsync(A, ReminderKind.HabitIncomplete, habit, Dentist)).Field);
        // Without the Habits port nothing can be verified: not found.
        Assert.Equal(ReminderResultStatus.NotFound, (await new CreateReminderHandler(_repository, _preferences, _clock).HandleAsync(A,
            new CreateReminderCommand(null, null, Daily0830, Rome, ReminderKind.HabitIncomplete, habit), CancellationToken.None)).Status);
        Assert.Empty(_repository.Reminders);

        var created = await CreateConditionalAsync(A, ReminderKind.HabitIncomplete, habit);
        Assert.Equal(ReminderResultStatus.Ok, created.Status);
        Assert.Equal(("If “Read 20 pages” isn't done", (string?)null, (Guid?)habit), (created.Value!.Label, created.Value.Reminder.Title, created.Value.Reminder.HabitId));

        var again = await CreateConditionalAsync(A, ReminderKind.HabitIncomplete, habit);
        Assert.Equal((ReminderResultStatus.Conflict, ReminderConflictException.ExistsForHabit), (again.Status, again.Code));
    }

    [Fact]
    public async Task CreateWaterReminder_OnePerUser_TitleAndNoteIgnored_AndCountsTowardsTheLimit()
    {
        var created = await CreateConditionalAsync(A, ReminderKind.HydrationGoal);
        Assert.Equal((ReminderResultStatus.Ok, "If today's water target isn't reached"), (created.Status, created.Value!.Label));

        var again = await CreateConditionalAsync(A, ReminderKind.HydrationGoal);
        Assert.Equal((ReminderResultStatus.Conflict, ReminderConflictException.HydrationExists), (again.Status, again.Code));
        Assert.Equal(ReminderResultStatus.Ok, (await CreateConditionalAsync(B, ReminderKind.HydrationGoal)).Status);
        Assert.Equal("habitId", (await CreateConditionalAsync(A, ReminderKind.HydrationGoal, Guid.CreateVersion7())).Field);

        // Editing changes the schedule only.
        var updated = await new UpdateReminderHandler(_repository, _preferences, _clock).HandleAsync(A, created.Value.Reminder.Id,
            new UpdateReminderCommand("Title", "Note", new ReminderScheduleInput(ReminderScheduleKind.Weekly, null, [DayOfWeek.Friday], new TimeOnly(17, 0)), Rome),
            CancellationToken.None);
        Assert.Equal(((string?)null, (string?)null, ReminderScheduleKind.Weekly), (updated.Value!.Reminder.Title, updated.Value.Reminder.Note,
            updated.Value.Reminder.ScheduleKind));

        for (var index = 1; index < ReminderLimits.MaxOpenReminders; index++)
        {
            await ReminderAsync(A, title: $"Reminder {index}");
        }

        var habit = Guid.CreateVersion7();
        _habits.Habits.Add((A, habit));
        Assert.Equal(ReminderLimits.LimitReachedCode, (await CreateConditionalAsync(A, ReminderKind.HabitIncomplete, habit)).Code);
    }

    [Fact]
    public async Task List_LabelsConditionalReminders_WithTheHabitsNameJoinedAtReadTime()
    {
        var habit = Guid.CreateVersion7();
        _habits.Habits.Add((A, habit));
        await CreateConditionalAsync(A, ReminderKind.HabitIncomplete, habit);
        await CreateConditionalAsync(A, ReminderKind.HydrationGoal);
        await ReminderAsync(A, title: "Take vitamins");

        _repository.HabitNames[(A, habit)] = "Stretch";
        var labels = (await new GetRemindersHandler(_repository, _preferences, _clock).HandleAsync(A, CancellationToken.None)).Upcoming
            .Select(view => view.Label).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["If today's water target isn't reached", "If “Stretch” isn't done", "Take vitamins"], labels);
    }

    private static (ReminderResultStatus, string?) Shape<T>(ReminderResult<T> result) => (result.Status, result.Field);
}
