using LifeOS.Application.Automation;
using LifeOS.Application.Habits;
using LifeOS.Application.Notifications;
using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Application.Reminders;
using LifeOS.Domain.Habits;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Nutrition.Hydration;
using LifeOS.Domain.Reminders;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Reminders;

// REM-001 R5: the conditional reminders (HabitIncomplete, HydrationGoal) over in-memory ports with the
// real Habits calculator (HabitDayStatusReader) and Hydration day rule (HydrationDayReader): the Domain
// shape, the one ReminderConditionEvaluator used by the execution and by the send-time relevance check,
// discovery split between the two handlers, and the HAB-001 §13 worked scenario on the real tick.
// Europe/Rome is UTC+2 until 25 Oct 2026; Wednesday 14 Oct 2026.
public class ConditionalReminderTests
{
    private const string Rome = "Europe/Rome";
    private const int RomeOffset = 120;
    private static readonly Guid A = TestUsers.A;
    private static readonly Guid B = TestUsers.B;

    private static readonly DateOnly Tuesday = new(2026, 10, 13);
    private static readonly DateOnly Wednesday = new(2026, 10, 14);

    // Created Tue 13 Oct 21:00 local: a Daily 20:00 is next due Wed 14 Oct 20:00 = 18:00Z.
    private static readonly DateTimeOffset Created = new(2026, 10, 13, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due = new(2026, 10, 14, 18, 0, 0, TimeSpan.Zero);

    private readonly InMemoryReminderRepository _reminders = new();
    private readonly InMemoryNotificationPreferencesRepository _preferences = new();
    private readonly InMemoryHabitRepository _habits = new();
    private readonly InMemoryHydrationRepository _water = new();
    private readonly InMemoryReminderScheduleStore _store;
    private readonly ManualTimeProvider _clock = new(Due);

    public ConditionalReminderTests()
    {
        _store = new InMemoryReminderScheduleStore(_reminders);
        _store.TimeZones[A] = Rome;
        _store.TimeZones[B] = Rome;
        _preferences.TimeZones[A] = Rome;
    }

    private ReminderConditionEvaluator Evaluator => new(new HabitDayStatusReader(_habits, new HydrationDayReader(_water)), new HydrationDayReader(_water));

    private ConditionalReminderAutomationHandler Handler => new(_store, _preferences, Evaluator, _clock);

    private ReminderRelevanceCheck Relevance => new(_store, _preferences, Evaluator);

    private static DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(Rome)).DateTime;

    private async Task<Habit> HabitAsync(Guid user, HabitFrequency? frequency = null, HabitCompletionSource source = HabitCompletionSource.Manual)
    {
        var habit = Habit.Create(user, "Read 20 pages", null, source, frequency ?? HabitFrequency.Daily, Tuesday.AddDays(-7), Tuesday, Created);
        await _habits.AddAsync(habit, default);
        return habit;
    }

    private Reminder AddReminder(Guid user, ReminderKind kind, Guid? habitId = null, ReminderSchedule? schedule = null)
    {
        var reminder = Reminder.CreateConditional(user, kind, habitId, schedule ?? ReminderSchedule.Daily(new TimeOnly(20, 0)), Local(Created), Created);
        _reminders.Reminders.Add(reminder);
        return reminder;
    }

    private Task CheckInAsync(Habit habit, DateOnly date, HabitCheckInStatus status) =>
        _habits.UpsertCheckInAsync(habit.CheckIn(date, status, null, Wednesday, Created), default);

    private Task SetWaterTargetAsync(Guid user, DateOnly from, int? targetMl) =>
        _water.UpsertTargetVersionAsync(HydrationTargetVersion.Create(user, from, targetMl, Created), default);

    private Task DrinkAsync(Guid user, DateOnly date, int ml) =>
        _water.AddAsync(HydrationIntake.Create(user, ml, date, new TimeOnly(12, 0), RomeOffset, _clock.UtcNow), default);

    private static AutomationOccurrence Occurrence(Reminder reminder, DateTime local) =>
        new(Guid.CreateVersion7(), reminder.UserId, ConditionalReminderAutomationHandler.Type,
            ReminderOccurrenceKey.Format(ReminderOccurrence.Scheduled(reminder.Id, local)), Rome, Due, Due + ConditionalReminderAutomationHandler.Lateness, 1);

    private static readonly DateTime WednesdayAt20 = new(2026, 10, 14, 20, 0, 0);

    private static NotificationRelevanceContext Context(Reminder reminder, DateTime local, NotificationType type) =>
        new(reminder.UserId, type, null, null, ReminderOccurrenceKey.Format(ReminderOccurrence.Scheduled(reminder.Id, local)), Due);

    // Executes the occurrence and applies its artifact (the reminder moves on), like the completion transaction.
    private async Task<AutomationResult.Success> ExecuteAsync(Reminder reminder, DateTime local)
    {
        var result = Assert.IsType<AutomationResult.Success>(await Handler.ExecuteAsync(Occurrence(reminder, local), default));
        Assert.True(await result.SaveArtifact!(default));
        return result;
    }

    // ---- Domain shape (§5) ----

    [Fact]
    public void ConditionalKinds_HaveNoText_RepeatAndNameTheirHabit()
    {
        var habitId = Guid.CreateVersion7();
        var habit = Reminder.CreateConditional(A, ReminderKind.HabitIncomplete, habitId, ReminderSchedule.Daily(new TimeOnly(20, 0)), Local(Created), Created);

        Assert.Equal((ReminderKind.HabitIncomplete, habitId, (string?)null, (string?)null, true), (habit.Kind, habit.HabitId, habit.Title, habit.Note, habit.IsConditional));
        Assert.Equal(WednesdayAt20, habit.NextDueLocal);

        Assert.Throws<ArgumentException>(() =>
            Reminder.CreateConditional(A, ReminderKind.HabitIncomplete, null, ReminderSchedule.Daily(new TimeOnly(20, 0)), Local(Created), Created));
        Assert.Throws<ArgumentException>(() =>
            Reminder.CreateConditional(A, ReminderKind.HydrationGoal, habitId, ReminderSchedule.Daily(new TimeOnly(17, 0)), Local(Created), Created));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Reminder.CreateConditional(A, ReminderKind.Custom, null, ReminderSchedule.Daily(new TimeOnly(17, 0)), Local(Created), Created));
        var once = Assert.Throws<ArgumentException>(() => Reminder.CreateConditional(A, ReminderKind.HydrationGoal, null,
            ReminderSchedule.Once(Wednesday, new TimeOnly(17, 0)), Local(Created), Created));
        Assert.Equal("schedule.kind", once.ParamName);

        // Editing changes the schedule only; title and note never apply.
        habit.Update("Sneaky title", "Sneaky note", ReminderSchedule.Weekly([DayOfWeek.Friday], new TimeOnly(21, 0)), Local(Created), Created);
        Assert.Equal(((string?)null, (string?)null, new DateTime(2026, 10, 16, 21, 0, 0)), (habit.Title, habit.Note, habit.NextDueLocal));
        Assert.Throws<ArgumentException>(() => habit.Update(null, null, ReminderSchedule.Once(Wednesday, new TimeOnly(21, 0)), Local(Created), Created));

        // A suppressed occurrence moves the schedule like a sent one.
        habit.Advance(new DateTime(2026, 10, 16, 21, 0, 0), ReminderOutcome.Suppressed, Due);
        Assert.Equal((ReminderOutcome.Suppressed, ReminderStatus.Active), (habit.LastOutcome, habit.Status));
    }

    // ---- Execution: HabitIncomplete (§6.3) ----

    [Fact]
    public async Task HabitIncomplete_NotifiesOnlyWhileTheHabitIsOpen()
    {
        var habit = await HabitAsync(A);
        var reminder = AddReminder(A, ReminderKind.HabitIncomplete, habit.Id);

        var open = await ExecuteAsync(reminder, WednesdayAt20);
        Assert.Equal(new AutomationNotification(NotificationType.HabitReminder, "habit", habit.Id), open.Notification);
        Assert.Equal((ReminderOutcome.Notified, new DateTime(2026, 10, 15, 20, 0, 0)), (_reminders.Find(A, reminder.Id)!.LastOutcome, _reminders.Find(A, reminder.Id)!.NextDueLocal));

        await CheckInAsync(habit, Wednesday, HabitCheckInStatus.Done);
        reminder = AddReminderCopyDue(_reminders.Find(A, reminder.Id)!);
        var done = await ExecuteAsync(reminder, WednesdayAt20);
        Assert.Null(done.Notification);
        Assert.Equal(ReminderOutcome.Suppressed, _reminders.Find(A, reminder.Id)!.LastOutcome);
    }

    // Puts the reminder back on the given occurrence (the stored copy moved on after the previous run).
    private Reminder AddReminderCopyDue(Reminder reminder)
    {
        _reminders.Reminders.RemoveAll(stored => stored.Id == reminder.Id);
        var fresh = Reminder.CreateConditional(reminder.UserId, reminder.Kind, reminder.HabitId, reminder.Schedule, Local(Created), Created);
        _reminders.Reminders.Add(fresh);
        return fresh;
    }

    public static TheoryData<string> ClosedHabitCases => ["Excused", "NotScheduled", "Archived", "WeekGoalMet", "Deleted"];

    [Theory]
    [MemberData(nameof(ClosedHabitCases))]
    public async Task HabitIncomplete_IsSuppressed_WhenTheHabitIsNotOpen(string @case)
    {
        var habit = await HabitAsync(A, @case switch
        {
            "NotScheduled" => HabitFrequency.OnWeekdays(WeekdaySet.Of([DayOfWeek.Monday])),
            "WeekGoalMet" => HabitFrequency.PerWeek(2),
            _ => HabitFrequency.Daily
        });
        var reminder = AddReminder(A, ReminderKind.HabitIncomplete, habit.Id);

        switch (@case)
        {
            case "Excused":
                await CheckInAsync(habit, Wednesday, HabitCheckInStatus.Excused);
                break;
            case "Archived":
                var stored = (await _habits.GetAsync(A, habit.Id, default))!;
                stored.Archive(Wednesday, Due);
                await _habits.SaveAsync(stored, default);
                break;
            case "WeekGoalMet":
                await CheckInAsync(habit, Wednesday.AddDays(-2), HabitCheckInStatus.Done);
                await CheckInAsync(habit, Tuesday, HabitCheckInStatus.Done);
                break;
            case "Deleted":
                await _habits.DeleteAsync(A, habit.Id, default);
                break;
        }

        var result = await ExecuteAsync(reminder, WednesdayAt20);

        Assert.Null(result.Notification);
        Assert.Equal(ReminderOutcome.Suppressed, _reminders.Find(A, reminder.Id)!.LastOutcome);
        Assert.False(await Relevance.IsStillRelevantAsync(Context(reminder, WednesdayAt20, NotificationType.HabitReminder), default));
    }

    [Fact]
    public async Task HabitIncomplete_ForAWaterHabit_FollowsHydration()
    {
        await SetWaterTargetAsync(A, Tuesday.AddDays(-7), 2000);
        var habit = await HabitAsync(A, source: HabitCompletionSource.Hydration);
        var reminder = AddReminder(A, ReminderKind.HabitIncomplete, habit.Id);
        await DrinkAsync(A, Wednesday, 1500);

        Assert.NotNull((await ExecuteAsync(reminder, WednesdayAt20)).Notification);

        await DrinkAsync(A, Wednesday, 500);
        Assert.False(await Relevance.IsStillRelevantAsync(Context(reminder, WednesdayAt20, NotificationType.HabitReminder), default));
    }

    // ---- Execution: HydrationGoal (§6.3) ----

    [Fact]
    public async Task HydrationGoal_NotifiesOnlyWithATargetNotYetReached()
    {
        var reminder = AddReminder(A, ReminderKind.HydrationGoal);

        // No target: nothing to remind about.
        Assert.Null((await ExecuteAsync(reminder, WednesdayAt20)).Notification);

        reminder = AddReminderCopyDue(reminder);
        await SetWaterTargetAsync(A, Tuesday, 2000);
        await DrinkAsync(A, Wednesday, 1200);
        // Another user's water never counts.
        await SetWaterTargetAsync(B, Tuesday, 500);
        await DrinkAsync(B, Wednesday, 3000);
        Assert.Equal(new AutomationNotification(NotificationType.HydrationReminder, "hydration", null), (await ExecuteAsync(reminder, WednesdayAt20)).Notification);

        reminder = AddReminderCopyDue(reminder);
        await DrinkAsync(A, Wednesday, 800);
        Assert.Null((await ExecuteAsync(reminder, WednesdayAt20)).Notification);
        Assert.Equal(ReminderOutcome.Suppressed, _reminders.Find(A, reminder.Id)!.LastOutcome);
    }

    [Fact]
    public async Task Execution_EvaluatesTheOccurrencesOwnDate_AndHonoursTheTypeSwitch()
    {
        // A 23:50 nudge executed after midnight still judges its own day.
        await SetWaterTargetAsync(A, Tuesday, 2000);
        var late = AddReminder(A, ReminderKind.HydrationGoal, schedule: ReminderSchedule.Daily(new TimeOnly(23, 50)));
        await DrinkAsync(A, Wednesday.AddDays(1), 2500);
        _clock.UtcNow = new DateTimeOffset(2026, 10, 14, 22, 10, 0, TimeSpan.Zero);

        Assert.NotNull((await ExecuteAsync(late, new DateTime(2026, 10, 14, 23, 50, 0))).Notification);
        Assert.True(await Relevance.IsStillRelevantAsync(Context(late, new DateTime(2026, 10, 14, 23, 50, 0), NotificationType.HydrationReminder), default));

        // The "Water reminders" switch off: nothing is sent, the reminder moves on as Disabled.
        _preferences.Set(A, hydration: false);
        var muted = AddReminderCopyDue(late);
        var result = await ExecuteAsync(muted, new DateTime(2026, 10, 14, 23, 50, 0));
        Assert.Null(result.Notification);
        Assert.Equal(ReminderOutcome.Disabled, _reminders.Find(A, muted.Id)!.LastOutcome);
    }

    [Fact]
    public async Task Execution_OfAnEditedPausedOrForeignKindOccurrence_IsNotApplicable()
    {
        var reminder = AddReminder(A, ReminderKind.HydrationGoal);
        Assert.IsType<AutomationResult.Inapplicable>(await Handler.ExecuteAsync(Occurrence(reminder, new DateTime(2026, 10, 14, 19, 0, 0)), default));

        var custom = Reminder.Create(A, "Take vitamins", null, ReminderSchedule.Daily(new TimeOnly(20, 0)), Local(Created), Created);
        _reminders.Reminders.Add(custom);
        Assert.IsType<AutomationResult.Inapplicable>(await Handler.ExecuteAsync(Occurrence(custom, WednesdayAt20), default));
        Assert.IsType<AutomationResult.Inapplicable>(await new ReminderAutomationHandler(_store, _preferences, _clock)
            .ExecuteAsync(Occurrence(reminder, WednesdayAt20) with { AutomationType = ReminderAutomationHandler.Type }, default));

        _reminders.Find(A, reminder.Id)!.Pause(Due);
        Assert.IsType<AutomationResult.Inapplicable>(await Handler.ExecuteAsync(Occurrence(reminder, WednesdayAt20), default));
        Assert.False(await Relevance.IsStillRelevantAsync(Context(reminder, WednesdayAt20, NotificationType.HydrationReminder), default));
    }

    // ---- Discovery (§6.2) ----

    [Fact]
    public async Task Discovery_SplitsTheKinds_BetweenTheTwoHandlers_AndSkipsMutedTypes()
    {
        var habit = await HabitAsync(A);
        var nudge = AddReminder(A, ReminderKind.HabitIncomplete, habit.Id);
        var water = AddReminder(A, ReminderKind.HydrationGoal);
        var custom = Reminder.Create(A, "Take vitamins", null, ReminderSchedule.Daily(new TimeOnly(20, 0)), Local(Created), Created);
        _reminders.Reminders.Add(custom);

        var conditional = await Handler.FindDueAsync(Due, 25, default);
        Assert.Equal(new[] { nudge.Id, water.Id }.Order(), conditional.Select(due => ParseId(due.OccurrenceKey)).Order());
        Assert.All(conditional, due => Assert.Equal(Due, due.ScheduledForUtc));
        Assert.Equal([custom.Id], (await new ReminderAutomationHandler(_store, _preferences, _clock).FindDueAsync(Due, 25, default)).Select(due => ParseId(due.OccurrenceKey)));

        _store.DisabledKinds.Add((A, ReminderKind.HydrationGoal));
        Assert.Equal([nudge.Id], (await Handler.FindDueAsync(Due, 25, default)).Select(due => ParseId(due.OccurrenceKey)));

        // Two hours late a nudge is skipped as Missed (D-4), not sent.
        _store.DisabledKinds.Clear();
        Assert.Empty(await Handler.FindDueAsync(Due + ConditionalReminderAutomationHandler.Lateness, 25, default));
        Assert.Equal(2, _store.Skipped.Single().Occurrences.Count);
    }

    private static Guid ParseId(string key) => ReminderOccurrenceKey.TryParse(key, out var occurrence) ? occurrence.ReminderId : Guid.Empty;

    // ---- Relevance check (§6.6) ----

    [Fact]
    public async Task RelevanceCheck_OwnsTheNudgeTypes_AndRejectsUnusableKeys()
    {
        Assert.Equal([NotificationType.HabitReminder, NotificationType.HydrationReminder], Relevance.Types.Order());

        var reminder = AddReminder(A, ReminderKind.HydrationGoal);
        await SetWaterTargetAsync(A, Tuesday, 2000);

        Assert.True(await Relevance.IsStillRelevantAsync(Context(reminder, WednesdayAt20, NotificationType.HydrationReminder), default));
        Assert.False(await Relevance.IsStillRelevantAsync(Context(reminder, WednesdayAt20, NotificationType.HydrationReminder) with { SourceOccurrenceKey = null }, default));
        Assert.False(await Relevance.IsStillRelevantAsync(Context(reminder, WednesdayAt20, NotificationType.HydrationReminder) with { SourceOccurrenceKey = "not-a-key" }, default));
        // Another user's delivery can't name this reminder.
        Assert.False(await Relevance.IsStillRelevantAsync(Context(reminder, WednesdayAt20, NotificationType.HydrationReminder) with { UserId = B }, default));

        _reminders.Reminders.Clear();
        Assert.False(await Relevance.IsStillRelevantAsync(Context(reminder, WednesdayAt20, NotificationType.HydrationReminder), default));
    }

    // The execution and the send-time check are one rule: for every state they agree.
    [Theory]
    [InlineData(0, null, false)]
    [InlineData(1999, 2000, true)]
    [InlineData(2000, 2000, false)]
    [InlineData(2600, 2000, false)]
    [InlineData(0, 1500, true)]
    public async Task ExecutionAndRelevance_UseTheSameEvaluator_ForWater(int totalMl, int? targetMl, bool needed)
    {
        if (targetMl is { } target)
        {
            await SetWaterTargetAsync(A, Tuesday, target);
        }

        if (totalMl > 0)
        {
            await DrinkAsync(A, Wednesday, totalMl);
        }

        var reminder = AddReminder(A, ReminderKind.HydrationGoal);
        var relevant = await Relevance.IsStillRelevantAsync(Context(reminder, WednesdayAt20, NotificationType.HydrationReminder), default);
        var executed = await ExecuteAsync(reminder, WednesdayAt20);

        Assert.Equal((needed, needed), (relevant, executed.Notification is not null));
    }

    // ---- Worked scenario (HAB-001 §13) on the real tick, outbox and dispatcher ----

    [Fact]
    public async Task WorkedScenario_SuppressedThenNotifiedThenNotRelevantBeforeTheSend()
    {
        var executions = new InMemoryAutomationExecutionStore();
        var devices = new InMemoryDeviceRegistrationRepository();
        var deliveries = new InMemoryNotificationDeliveryStore(devices)
        {
            OccurrenceKeyOf = id => executions.Rows.SingleOrDefault(row => row.Id == id)?.OccurrenceKey
        };
        var sender = new FakePushNotificationSender();
        var unitOfWork = new FakeUnitOfWork();

        RunAutomationTick Tick() => new([Handler], executions,
            new NotificationDispatcher(deliveries, devices, _preferences, unitOfWork, _clock, sender, [Relevance]), deliveries, unitOfWork,
            new AutomationTickGuard(), _clock);

        _clock.UtcNow = Created;
        await devices.UpsertAsync(DeviceRegistration.Register(A, "phone-installation-0001", DevicePlatform.Android, "token-a", true, Created), default);
        await SetWaterTargetAsync(A, Tuesday, 2000);
        var habit = await HabitAsync(A, source: HabitCompletionSource.Hydration);
        var reminder = AddReminder(A, ReminderKind.HabitIncomplete, habit.Id);

        // Wednesday 14: the target is met at 18:30; the 20:00 occurrence is Suppressed; nothing is enqueued.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 14, 16, 30, 0, TimeSpan.Zero);
        await DrinkAsync(A, Wednesday, 250);
        await DrinkAsync(A, Wednesday, 1750);
        _clock.UtcNow = new DateTimeOffset(2026, 10, 14, 18, 0, 0, TimeSpan.Zero);
        await Tick().RunAsync();

        Assert.Empty(deliveries.Rows);
        Assert.Equal((ReminderOutcome.Suppressed, new DateTime(2026, 10, 15, 20, 0, 0)),
            (_reminders.Find(A, reminder.Id)!.LastOutcome, _reminders.Find(A, reminder.Id)!.NextDueLocal));

        // Thursday 15: 1000 ml at 20:00 → Notified; the next tick re-checks (still open) and sends.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 15, 13, 0, 0, TimeSpan.Zero);
        await DrinkAsync(A, Wednesday.AddDays(1), 1000);
        _clock.UtcNow = new DateTimeOffset(2026, 10, 15, 18, 0, 0, TimeSpan.Zero);
        await Tick().RunAsync();
        var thursday = Assert.Single(deliveries.Rows);
        Assert.Equal((NotificationType.HabitReminder, "habit", (Guid?)habit.Id), (thursday.Type, thursday.ResourceType, thursday.ResourceId));
        Assert.Equal(ReminderOutcome.Notified, _reminders.Find(A, reminder.Id)!.LastOutcome);

        _clock.UtcNow = new DateTimeOffset(2026, 10, 15, 18, 10, 0, TimeSpan.Zero);
        await Tick().RunAsync();
        var (_, message) = Assert.Single(sender.Sent);
        Assert.Equal(("A habit is still open today", "habit", habit.Id.ToString("D")), (message.Body, message.Data["type"], message.Data["id"]));
        Assert.DoesNotContain("Read", message.Body);
        Assert.Equal(NotificationDeliveryStatus.Sent, thursday.Status);

        // Friday 16: 1500 ml at 20:00 → enqueued; +500 at 20:04 → the send-time check finds it done.
        _clock.UtcNow = new DateTimeOffset(2026, 10, 16, 13, 0, 0, TimeSpan.Zero);
        await DrinkAsync(A, Wednesday.AddDays(2), 1500);
        _clock.UtcNow = new DateTimeOffset(2026, 10, 16, 18, 0, 0, TimeSpan.Zero);
        await Tick().RunAsync();
        var friday = deliveries.Rows.Single(row => row != thursday);
        Assert.Equal(NotificationDeliveryStatus.Pending, friday.Status);

        _clock.UtcNow = new DateTimeOffset(2026, 10, 16, 18, 4, 0, TimeSpan.Zero);
        await DrinkAsync(A, Wednesday.AddDays(2), 500);
        _clock.UtcNow = new DateTimeOffset(2026, 10, 16, 18, 10, 0, TimeSpan.Zero);
        await Tick().RunAsync();

        Assert.Single(sender.Sent);
        Assert.Equal((NotificationDeliveryStatus.Failed, (NotificationErrorCode?)NotificationErrorCode.NotRelevant), (friday.Status, friday.LastErrorCode));
        // The execution produced the logical notification: the reminder's outcome stays Notified.
        Assert.Equal(ReminderOutcome.Notified, _reminders.Find(A, reminder.Id)!.LastOutcome);

        // Nothing about water or the habit was written by Reminders or Habits.
        Assert.Empty(_habits.CheckIns);
    }
}
