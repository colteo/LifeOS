using LifeOS.Application.Automation;
using LifeOS.Application.Reminders;
using LifeOS.Domain.Automation;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Reminders;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Reminders;

// REM-001 §6 (R3): discovery and execution of the user's own reminders over in-memory ports. The
// discovery SQL, the completion transaction and the real tick are covered in ReminderTickTests.
// Europe/Rome is UTC+2 until 25 Oct 2026; Wednesday 14 Oct 2026.
public class ReminderAutomationHandlerTests
{
    private const string Rome = "Europe/Rome";
    private static readonly Guid A = TestUsers.A;
    private static readonly Guid B = TestUsers.B;

    // Created on Tue 13 Oct 09:00 local (07:00Z): a Daily 08:30 is next due Wed 14 Oct 08:30 = 06:30Z.
    private static readonly DateTimeOffset Created = new(2026, 10, 13, 7, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due = new(2026, 10, 14, 6, 30, 0, TimeSpan.Zero);

    private readonly InMemoryReminderRepository _repository = new();
    private readonly InMemoryNotificationPreferencesRepository _preferences = new();
    private readonly InMemoryReminderScheduleStore _store;
    private readonly ManualTimeProvider _clock = new(Due);

    public ReminderAutomationHandlerTests()
    {
        _store = new InMemoryReminderScheduleStore(_repository);
        _store.TimeZones[A] = Rome;
    }

    private ReminderAutomationHandler Handler => new(_store, _preferences, _clock);

    private Reminder Add(Guid user, ReminderSchedule? schedule = null, DateTimeOffset? created = null)
    {
        var at = created ?? Created;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Rome);
        var reminder = Reminder.Create(user, "Take vitamins", "Secret note", schedule ?? ReminderSchedule.Daily(new TimeOnly(8, 30)),
            TimeZoneInfo.ConvertTime(at, zone).DateTime, at);
        _repository.Reminders.Add(reminder);
        return reminder;
    }

    private Task<IReadOnlyList<DueOccurrence>> FindAsync(DateTimeOffset now, int limit = 25) => Handler.FindDueAsync(now, limit, default);

    private static AutomationOccurrence Occurrence(Guid user, string key, string zone = Rome) =>
        new(Guid.CreateVersion7(), user, ReminderAutomationHandler.Type, key, zone, Due, Due + ReminderAutomationHandler.Lateness, 1);

    // ---- Occurrence key ----

    [Fact]
    public void OccurrenceKey_RoundTrips_AndIsAStableCode()
    {
        var id = new Guid("0192f0e4-11aa-7b20-9f00-6c1d00000007");
        var scheduled = ReminderOccurrence.Scheduled(id, new DateTime(2026, 10, 20, 10, 0, 0));
        var snooze = ReminderOccurrence.Snooze(id, new DateTimeOffset(2026, 10, 20, 8, 15, 0, TimeSpan.Zero));

        Assert.Equal("0192f0e4-11aa-7b20-9f00-6c1d00000007:2026-10-20T10:00", ReminderOccurrenceKey.Format(scheduled));
        Assert.Equal("0192f0e4-11aa-7b20-9f00-6c1d00000007:s:2026-10-20T08:15Z", ReminderOccurrenceKey.Format(snooze));

        foreach (var occurrence in new[] { scheduled, snooze })
        {
            var key = ReminderOccurrenceKey.Format(occurrence);
            Assert.True(AutomationExecution.IsStableCode(key, AutomationExecution.MaxOccurrenceKeyLength));
            Assert.True(ReminderOccurrenceKey.TryParse(key, out var parsed));
            Assert.Equal(occurrence, parsed);
        }

        Assert.Equal(TimeSpan.Zero, (ReminderOccurrenceKey.TryParse(ReminderOccurrenceKey.Format(snooze), out var back) ? back : null)!.SnoozeUtc!.Value.Offset);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("00000000-0000-0000-0000-000000000000:2026-10-20T10:00")]
    [InlineData("0192f0e4-11aa-7b20-9f00-6c1d00000007:2026-10-20")]
    [InlineData("0192f0e4-11aa-7b20-9f00-6c1d00000007:2026-13-20T10:00")]
    [InlineData("0192f0e4-11aa-7b20-9f00-6c1d00000007:2026-10-20T10:00:00")]
    [InlineData("0192f0e4-11aa-7b20-9f00-6c1d00000007:x:2026-10-20T08:15Z")]
    [InlineData("0192f0e4-11aa-7b20-9f00-6c1d00000007:s:2026-10-20T08:15+")]
    [InlineData("0192F0E4-11AA-7B20-9F00-6C1D00000007;2026-10-20T10:00")]
    public void OccurrenceKey_RejectsAnythingElse(string? key) => Assert.False(ReminderOccurrenceKey.TryParse(key, out _));

    // ---- Discovery ----

    [Fact]
    public async Task Discovery_AtTheLocalTime_FindsTheOccurrence_WithItsUtcInstant()
    {
        var reminder = Add(A);

        Assert.Empty(await FindAsync(Due.AddMinutes(-1)));

        var found = Assert.Single(await FindAsync(Due.AddMinutes(5)));
        Assert.Equal(new DueOccurrence(A, $"{reminder.Id:D}:2026-10-14T08:30", Rome, Due), found);
        Assert.Empty(_store.Skipped);
    }

    [Fact]
    public async Task Discovery_AfterAnOutage_DeliversOnlyTheLatestOccurrence()
    {
        var reminder = Add(A, created: Created.AddDays(-3));
        Assert.Equal(new DateTime(2026, 10, 11, 8, 30, 0), reminder.NextDueLocal);

        var found = Assert.Single(await FindAsync(Due.AddMinutes(10)));

        Assert.Equal($"{reminder.Id:D}:2026-10-14T08:30", found.OccurrenceKey);
        Assert.Equal(Due, found.ScheduledForUtc);
    }

    [Fact]
    public async Task Discovery_BeyondTheLateness_SkipsTheOccurrenceAsMissed()
    {
        var reminder = Add(A);

        Assert.Single(await FindAsync(Due + ReminderAutomationHandler.Lateness - TimeSpan.FromMinutes(1)));
        Assert.Empty(await FindAsync(Due + ReminderAutomationHandler.Lateness));

        var (missed, snoozes) = Assert.Single(_store.Skipped);
        Assert.Equal([new MissedOccurrence(A, reminder.Id, new DateTime(2026, 10, 14, 8, 30, 0), new DateTime(2026, 10, 15, 8, 30, 0))], missed);
        Assert.Empty(snoozes);
    }

    [Fact]
    public async Task Discovery_AMissedOnceReminder_CompletesIt()
    {
        var reminder = Add(A, ReminderSchedule.Once(new DateOnly(2026, 10, 14), new TimeOnly(8, 30)));

        Assert.Empty(await FindAsync(Due.AddHours(7)));

        Assert.Equal([new MissedOccurrence(A, reminder.Id, new DateTime(2026, 10, 14, 8, 30, 0), null)], Assert.Single(_store.Skipped).Occurrences);
    }

    [Fact]
    public async Task Discovery_TerminalExecution_IsSkipped_RunningOrRetryingIsLeftToTheCore()
    {
        var done = Add(A);
        var running = Add(A);
        _store.Executions[$"{done.Id:D}:2026-10-14T08:30"] = true;
        _store.Executions[$"{running.Id:D}:2026-10-14T08:30"] = false;

        Assert.Empty(await FindAsync(Due.AddMinutes(5)));

        Assert.Equal([new MissedOccurrence(A, done.Id, new DateTime(2026, 10, 14, 8, 30, 0), new DateTime(2026, 10, 15, 8, 30, 0))],
            Assert.Single(_store.Skipped).Occurrences);
    }

    [Fact]
    public async Task Discovery_IgnoresUsersWithoutAValidZone_PausedReminders_AndMutedUsers()
    {
        Add(B);                                     // no zone
        var invalidUser = Guid.CreateVersion7();
        _store.TimeZones[invalidUser] = "Mars/Olympus";
        Add(invalidUser);
        var paused = Add(A);
        paused.Pause(Created);
        var muted = Guid.CreateVersion7();
        _store.TimeZones[muted] = Rome;
        _store.DisabledUsers.Add(muted);
        Add(muted);

        Assert.Empty(await FindAsync(Due.AddMinutes(5)));
    }

    [Fact]
    public async Task Discovery_FindsADueSnooze_AndSkipsAStaleOne()
    {
        var once = Add(A, ReminderSchedule.Once(new DateOnly(2026, 10, 20), new TimeOnly(10, 0)));
        once.Snooze(15, Due);
        var snoozeAt = Due.AddMinutes(15);

        Assert.Empty(await FindAsync(snoozeAt.AddMinutes(-1)));

        var found = Assert.Single(await FindAsync(snoozeAt.AddMinutes(3)));
        Assert.Equal(new DueOccurrence(A, $"{once.Id:D}:s:2026-10-14T06:45Z", Rome, snoozeAt), found);

        Assert.Empty(await FindAsync(snoozeAt + ReminderAutomationHandler.Lateness));
        Assert.Equal([new MissedSnooze(A, once.Id, snoozeAt)], Assert.Single(_store.Skipped).Snoozes);
    }

    [Fact]
    public async Task Discovery_InASpringForwardGap_IsDueWhenTheGapEnds()
    {
        // Europe/Rome, Sun 29 Mar 2026: 02:00 CET → 03:00 CEST. 02:30 does not exist: due at 03:30 CEST = 01:30Z.
        Add(A, ReminderSchedule.Daily(new TimeOnly(2, 30)), created: new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero));

        Assert.Empty(await FindAsync(new DateTimeOffset(2026, 3, 29, 1, 10, 0, TimeSpan.Zero)));   // 03:10 CEST

        var found = Assert.Single(await FindAsync(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero)));
        Assert.EndsWith(":2026-03-29T02:30", found.OccurrenceKey);
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), found.ScheduledForUtc);
    }

    [Fact]
    public async Task Discovery_OnAFallBackDay_HasOneOccurrence_AtTheFirst0230()
    {
        // Sun 25 Oct 2026: 03:00 CEST → 02:00 CET; 02:30 happens twice (00:30Z and 01:30Z).
        var reminder = Add(A, ReminderSchedule.Daily(new TimeOnly(2, 30)), created: new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

        var first = Assert.Single(await FindAsync(new DateTimeOffset(2026, 10, 25, 0, 35, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), first.ScheduledForUtc);

        // After it ran (the reminder moved on), the second 02:30 finds nothing.
        _repository.Find(A, reminder.Id)!.Advance(new DateTime(2026, 10, 25, 2, 30, 0), ReminderOutcome.Notified, first.ScheduledForUtc);
        Assert.Empty(await FindAsync(new DateTimeOffset(2026, 10, 25, 1, 35, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Discovery_FollowsTheUsersCurrentZone()
    {
        // Daily 08:30 set in Rome; the phone is now in New York (UTC-4): 08:30 there is 12:30Z.
        var reminder = Add(A);
        _store.TimeZones[A] = "America/New_York";

        Assert.Empty(await FindAsync(Due.AddMinutes(5)));

        var found = Assert.Single(await FindAsync(new DateTimeOffset(2026, 10, 14, 12, 30, 0, TimeSpan.Zero)));
        Assert.Equal(($"{reminder.Id:D}:2026-10-14T08:30", "America/New_York"), (found.OccurrenceKey, found.TimeZoneId));
    }

    [Fact]
    public async Task Discovery_RespectsTheLimit()
    {
        for (var index = 0; index < 5; index++)
        {
            Add(A);
        }

        Assert.Equal(3, (await FindAsync(Due.AddMinutes(5), limit: 3)).Count);
        Assert.Empty(await FindAsync(Due.AddMinutes(5), limit: 0));
    }

    // ---- Execution ----

    [Fact]
    public async Task Execute_NotifiesWithFixedCopy_AndMovesTheReminderOn()
    {
        var reminder = Add(A);
        _clock.UtcNow = Due.AddMinutes(5);

        var result = Assert.IsType<AutomationResult.Success>(await Handler.ExecuteAsync(Occurrence(A, $"{reminder.Id:D}:2026-10-14T08:30"), default));

        // Only the type and the opaque id reach the push: never the title or the note.
        Assert.Equal(new AutomationNotification(NotificationType.UserReminder, "reminder", reminder.Id), result.Notification);
        Assert.Null(result.ResultId);
        Assert.Equal(new DateTime(2026, 10, 14, 8, 30, 0), _repository.Find(A, reminder.Id)!.NextDueLocal);

        Assert.True(await result.SaveArtifact!(default));

        var stored = _repository.Find(A, reminder.Id)!;
        Assert.Equal(((DateTime?)new DateTime(2026, 10, 15, 8, 30, 0), (ReminderOutcome?)ReminderOutcome.Notified, (DateTimeOffset?)Due.AddMinutes(5)),
            (stored.NextDueLocal, stored.LastOutcome, stored.LastTriggeredAtUtc));
    }

    [Fact]
    public async Task Execute_TheLastOnceOccurrence_CompletesTheReminder()
    {
        var reminder = Add(A, ReminderSchedule.Once(new DateOnly(2026, 10, 14), new TimeOnly(8, 30)));

        var result = Assert.IsType<AutomationResult.Success>(await Handler.ExecuteAsync(Occurrence(A, $"{reminder.Id:D}:2026-10-14T08:30"), default));
        Assert.True(await result.SaveArtifact!(default));

        Assert.Equal((ReminderStatus.Completed, (DateTime?)null), (_repository.Find(A, reminder.Id)!.Status, _repository.Find(A, reminder.Id)!.NextDueLocal));
    }

    [Fact]
    public async Task Execute_AfterTheSwitchWasTurnedOff_SendsNothing_ButMovesOn()
    {
        var reminder = Add(A);
        _preferences.Set(A, user: false);

        var result = Assert.IsType<AutomationResult.Success>(await Handler.ExecuteAsync(Occurrence(A, $"{reminder.Id:D}:2026-10-14T08:30"), default));

        Assert.Null(result.Notification);
        Assert.True(await result.SaveArtifact!(default));
        Assert.Equal(((DateTime?)new DateTime(2026, 10, 15, 8, 30, 0), (ReminderOutcome?)ReminderOutcome.Disabled),
            (_repository.Find(A, reminder.Id)!.NextDueLocal, _repository.Find(A, reminder.Id)!.LastOutcome));
    }

    [Fact]
    public async Task Execute_ASnooze_NotifiesAndClearsIt()
    {
        var reminder = Add(A);
        reminder.Snooze(60, Due);

        var result = Assert.IsType<AutomationResult.Success>(await Handler.ExecuteAsync(Occurrence(A, $"{reminder.Id:D}:s:2026-10-14T07:30Z"), default));

        Assert.Equal(NotificationType.UserReminder, result.Notification!.Type);
        Assert.True(await result.SaveArtifact!(default));
        Assert.Null(_repository.Find(A, reminder.Id)!.SnoozeUntilUtc);
        Assert.Equal(new DateTime(2026, 10, 14, 8, 30, 0), _repository.Find(A, reminder.Id)!.NextDueLocal);
    }

    [Fact]
    public async Task Execute_ChangedSinceDiscovery_IsNotApplicable()
    {
        var deleted = Guid.CreateVersion7();
        var paused = Add(A);
        paused.Pause(Created);
        var edited = Add(A);
        edited.Update("Take vitamins", null, ReminderSchedule.Daily(new TimeOnly(21, 0)), new DateTime(2026, 10, 14, 8, 0, 0), Created);
        var resnoozed = Add(A);
        resnoozed.Snooze(15, Due.AddMinutes(30));
        var foreign = Add(B);

        Assert.IsType<AutomationResult.Inapplicable>(await Handler.ExecuteAsync(Occurrence(A, $"{deleted:D}:2026-10-14T08:30"), default));
        Assert.IsType<AutomationResult.Inapplicable>(await Handler.ExecuteAsync(Occurrence(A, $"{paused.Id:D}:2026-10-14T08:30"), default));
        Assert.IsType<AutomationResult.Inapplicable>(await Handler.ExecuteAsync(Occurrence(A, $"{edited.Id:D}:2026-10-14T08:30"), default));
        Assert.IsType<AutomationResult.Inapplicable>(await Handler.ExecuteAsync(Occurrence(A, $"{resnoozed.Id:D}:s:2026-10-14T06:45Z"), default));
        Assert.IsType<AutomationResult.Inapplicable>(await Handler.ExecuteAsync(Occurrence(A, $"{foreign.Id:D}:2026-10-14T08:30"), default));
        Assert.Empty(_store.Advanced);
    }

    [Fact]
    public async Task Execute_InvalidKeyOrZone_IsAPermanentFailure()
    {
        var reminder = Add(A);

        Assert.Equal(ReminderAutomationHandler.InvalidOccurrenceKeyCode,
            Assert.IsType<AutomationResult.Permanent>(await Handler.ExecuteAsync(Occurrence(A, "garbage"), default)).Code);
        Assert.Equal(ReminderAutomationHandler.InvalidTimeZoneCode,
            Assert.IsType<AutomationResult.Permanent>(await Handler.ExecuteAsync(Occurrence(A, $"{reminder.Id:D}:2026-10-14T08:30", "Nowhere/Land"), default)).Code);
    }

    [Fact]
    public void Handler_UsesItsOwnTypeAndA6HourLateness()
    {
        Assert.Equal(("Reminder", TimeSpan.FromHours(6)), (Handler.AutomationType, Handler.MaxLateness));
    }
}
