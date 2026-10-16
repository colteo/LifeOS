using LifeOS.Domain.Reminders;

namespace LifeOS.UnitTests.Reminders;

// REM-001 §5: the Reminder aggregate. Wednesday 14 Oct 2026, 09:00 local (07:00Z) is "now".
public class ReminderTests
{
    private static readonly Guid User = new("01a0ea76-0c00-7000-8000-0000000000b1");
    private static readonly DateTime LocalNow = new(2026, 10, 14, 9, 0, 0);
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 7, 0, 0, TimeSpan.Zero);

    private static readonly ReminderSchedule Daily0830 = ReminderSchedule.Daily(new TimeOnly(8, 30));
    private static readonly ReminderSchedule Dentist = ReminderSchedule.Once(new DateOnly(2026, 10, 20), new TimeOnly(10, 0));

    private static Reminder Create(ReminderSchedule? schedule = null) =>
        Reminder.Create(User, "Take vitamins", null, schedule ?? Daily0830, LocalNow, Now);

    // ---- Create ----

    [Fact]
    public void Create_TrimsText_StartsActive_WithTheNextOccurrence()
    {
        var reminder = Reminder.Create(User, "  Call the dentist ", "  Ask about Friday  ", Dentist, LocalNow.AddSeconds(42), Now.AddTicks(1234567));

        Assert.Equal(7, reminder.Id.Version);
        Assert.Equal((User, ReminderKind.Custom, "Call the dentist", "Ask about Friday"), (reminder.UserId, reminder.Kind, reminder.Title, reminder.Note));
        Assert.Equal(Dentist, reminder.Schedule);
        Assert.Equal((ReminderStatus.Active, (DateTime?)new DateTime(2026, 10, 20, 10, 0, 0)), (reminder.Status, reminder.NextDueLocal));
        Assert.Equal(((DateTimeOffset?)null, (DateTimeOffset?)null, (ReminderOutcome?)null),
            (reminder.SnoozeUntilUtc, reminder.LastTriggeredAtUtc, reminder.LastOutcome));
        Assert.Equal(0, reminder.CreatedAtUtc.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.Equal(reminder.CreatedAtUtc, reminder.UpdatedAtUtc);
    }

    [Fact]
    public void Create_Daily_BeforeTodaysTimeHasPassed_IsDueToday_OtherwiseTomorrow()
    {
        Assert.Equal(new DateTime(2026, 10, 15, 8, 30, 0), Create().NextDueLocal);
        Assert.Equal(new DateTime(2026, 10, 14, 9, 30, 0), Create(ReminderSchedule.Daily(new TimeOnly(9, 30))).NextDueLocal);

        // Seconds are ignored: created at 09:00:59 for 09:00 means tomorrow (strictly after the minute).
        Assert.Equal(new DateTime(2026, 10, 15, 9, 0, 0),
            Reminder.Create(User, "x", null, ReminderSchedule.Daily(new TimeOnly(9, 0)), LocalNow.AddSeconds(59), Now).NextDueLocal);
    }

    [Theory]
    [InlineData("", null, "title")]
    [InlineData("   ", null, "title")]
    [InlineData("81", null, "title")]
    [InlineData("ok", "501", "note")]
    public void Create_RejectsInvalidText(string title, string? note, string field)
    {
        title = title == "81" ? new string('a', 81) : title;
        note = note == "501" ? new string('n', 501) : note;

        Assert.Equal(field, Assert.Throws<ArgumentException>(() => Reminder.Create(User, title, note, Daily0830, LocalNow, Now)).ParamName);
    }

    [Fact]
    public void Create_AcceptsTheMaximumLengths_AndBlankNoteIsNoNote()
    {
        var reminder = Reminder.Create(User, new string('a', 80), new string('n', 500), Daily0830, LocalNow, Now);
        Assert.Equal((80, 500), (reminder.Title.Length, reminder.Note!.Length));

        Assert.Null(Reminder.Create(User, "x", "   ", Daily0830, LocalNow, Now).Note);
    }

    [Fact]
    public void Create_OnceInThePast_OrNow_IsRejected()
    {
        Assert.Equal("schedule.date", Assert.Throws<ArgumentException>(() =>
            Create(ReminderSchedule.Once(new DateOnly(2026, 10, 14), new TimeOnly(9, 0)))).ParamName);
        Assert.Equal("schedule.date", Assert.Throws<ArgumentException>(() =>
            Create(ReminderSchedule.Once(new DateOnly(2026, 10, 13), new TimeOnly(23, 0)))).ParamName);
        Assert.Equal(new DateTime(2026, 10, 14, 9, 1, 0), Create(ReminderSchedule.Once(new DateOnly(2026, 10, 14), new TimeOnly(9, 1))).NextDueLocal);
    }

    [Fact]
    public void Create_RequiresAUser() =>
        Assert.Throws<ArgumentException>(() => Reminder.Create(Guid.Empty, "x", null, Daily0830, LocalNow, Now));

    // ---- Update ----

    [Fact]
    public void Update_RecomputesFromNow_AndDropsASnooze()
    {
        var reminder = Create();
        reminder.Snooze(15, Now);

        reminder.Update("Take vitamin D", "With food", ReminderSchedule.Weekly([DayOfWeek.Friday], new TimeOnly(20, 0)), LocalNow.AddHours(1), Now.AddHours(1));

        Assert.Equal(("Take vitamin D", "With food"), (reminder.Title, reminder.Note));
        Assert.Equal(new DateTime(2026, 10, 16, 20, 0, 0), reminder.NextDueLocal);
        Assert.Null(reminder.SnoozeUntilUtc);
        Assert.Equal(Now.AddHours(1), reminder.UpdatedAtUtc);
    }

    [Fact]
    public void Update_OfACompletedReminder_ToANewDate_ActivatesIt()
    {
        var reminder = Create(Dentist);
        reminder.Advance(new DateTime(2026, 10, 20, 10, 0, 0), ReminderOutcome.Notified, Now.AddDays(6));
        Assert.Equal(ReminderStatus.Completed, reminder.Status);

        reminder.Update("Call the dentist", null, ReminderSchedule.Once(new DateOnly(2026, 10, 27), new TimeOnly(10, 0)), LocalNow.AddDays(7), Now.AddDays(7));

        Assert.Equal((ReminderStatus.Active, (DateTime?)new DateTime(2026, 10, 27, 10, 0, 0)), (reminder.Status, reminder.NextDueLocal));
    }

    [Fact]
    public void Update_OfAPausedReminder_StaysPaused()
    {
        var reminder = Create();
        reminder.Pause(Now);

        reminder.Update("Take vitamins", null, ReminderSchedule.Daily(new TimeOnly(10, 0)), LocalNow, Now);

        Assert.Equal((ReminderStatus.Paused, (DateTime?)new DateTime(2026, 10, 14, 10, 0, 0)), (reminder.Status, reminder.NextDueLocal));
    }

    [Fact]
    public void Update_Invalid_ChangesNothing()
    {
        var reminder = Create();

        Assert.Throws<ArgumentException>(() => reminder.Update("", null, ReminderSchedule.Daily(new TimeOnly(7, 0)), LocalNow, Now));
        Assert.Throws<ArgumentException>(() => reminder.Update("New", null, ReminderSchedule.Once(new DateOnly(2026, 10, 1), new TimeOnly(7, 0)), LocalNow, Now));

        Assert.Equal(("Take vitamins", Daily0830, (DateTime?)new DateTime(2026, 10, 15, 8, 30, 0)), (reminder.Title, reminder.Schedule, reminder.NextDueLocal));
    }

    // ---- Pause / Resume ----

    [Fact]
    public void PauseResume_DoesNotCatchUp()
    {
        var reminder = Create();
        reminder.Snooze(60, Now);

        reminder.Pause(Now);
        reminder.Pause(Now.AddMinutes(1));   // idempotent
        Assert.Equal((ReminderStatus.Paused, (DateTimeOffset?)null, Now), (reminder.Status, reminder.SnoozeUntilUtc, reminder.UpdatedAtUtc));

        // Three days later at 12:00: the next occurrence is tomorrow 08:30, nothing in between.
        reminder.Resume(LocalNow.AddDays(3).AddHours(3), Now.AddDays(3));
        Assert.Equal((ReminderStatus.Active, (DateTime?)new DateTime(2026, 10, 18, 8, 30, 0)), (reminder.Status, reminder.NextDueLocal));

        reminder.Resume(LocalNow.AddDays(4), Now.AddDays(4));   // idempotent while active
        Assert.Equal(new DateTime(2026, 10, 18, 8, 30, 0), reminder.NextDueLocal);
    }

    [Fact]
    public void Resume_OfAOnceReminderWhoseTimePassed_CompletesItAsMissed()
    {
        var reminder = Create(Dentist);
        reminder.Pause(Now);

        reminder.Resume(new DateTime(2026, 10, 21, 9, 0, 0), Now.AddDays(7));

        Assert.Equal((ReminderStatus.Completed, (DateTime?)null, (ReminderOutcome?)ReminderOutcome.Missed),
            (reminder.Status, reminder.NextDueLocal, reminder.LastOutcome));
    }

    [Fact]
    public void Completed_CannotBePausedOrResumed()
    {
        var reminder = Create(Dentist);
        reminder.Advance(new DateTime(2026, 10, 20, 10, 0, 0), ReminderOutcome.Notified, Now);

        Assert.Equal(ReminderConflictException.Completed, Assert.Throws<ReminderConflictException>(() => reminder.Pause(Now)).Code);
        Assert.Equal(ReminderConflictException.Completed, Assert.Throws<ReminderConflictException>(() => reminder.Resume(LocalNow, Now)).Code);
    }

    // ---- Snooze ----

    [Theory]
    [InlineData(15)]
    [InlineData(60)]
    [InlineData(180)]
    public void Snooze_IsWholeMinutesFromNow_AndReplacesAPendingOne(int minutes)
    {
        var reminder = Create();
        reminder.Snooze(15, Now);

        reminder.Snooze(minutes, Now.AddSeconds(95.5));

        Assert.Equal(new DateTimeOffset(2026, 10, 14, 7, 1, 0, TimeSpan.Zero).AddMinutes(minutes), reminder.SnoozeUntilUtc);
        Assert.Equal(new DateTime(2026, 10, 15, 8, 30, 0), reminder.NextDueLocal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(-15)]
    public void Snooze_OtherDurations_AreRejected(int minutes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Create().Snooze(minutes, Now));

    [Fact]
    public void Snooze_PausedIsRejected_CompletedIsAllowed()
    {
        var paused = Create();
        paused.Pause(Now);
        Assert.Equal(ReminderConflictException.Paused, Assert.Throws<ReminderConflictException>(() => paused.Snooze(15, Now)).Code);

        var completed = Create(Dentist);
        completed.Advance(new DateTime(2026, 10, 20, 10, 0, 0), ReminderOutcome.Notified, Now);
        completed.Snooze(60, Now);
        Assert.Equal((ReminderStatus.Completed, (DateTimeOffset?)Now.AddMinutes(60)), (completed.Status, completed.SnoozeUntilUtc));
    }

    // ---- Advance ----

    [Fact]
    public void Advance_MovesToTheNextOccurrence_AndRecordsTheOutcome()
    {
        var reminder = Create();

        Assert.True(reminder.Advance(new DateTime(2026, 10, 15, 8, 30, 0), ReminderOutcome.Notified, Now.AddDays(1)));

        Assert.Equal((ReminderStatus.Active, (DateTime?)new DateTime(2026, 10, 16, 8, 30, 0)), (reminder.Status, reminder.NextDueLocal));
        Assert.Equal(((DateTimeOffset?)Now.AddDays(1), (ReminderOutcome?)ReminderOutcome.Notified), (reminder.LastTriggeredAtUtc, reminder.LastOutcome));
    }

    [Fact]
    public void Advance_PastSkippedOccurrences_GoesAfterTheExecutedOne()
    {
        var reminder = Create();

        // Due since the 15th; the occurrence executed is the 17th (the 15th and 16th were skipped).
        Assert.True(reminder.Advance(new DateTime(2026, 10, 17, 8, 30, 0), ReminderOutcome.Notified, Now.AddDays(3)));

        Assert.Equal(new DateTime(2026, 10, 18, 8, 30, 0), reminder.NextDueLocal);
    }

    [Fact]
    public void Advance_OfTheLastOnceOccurrence_Completes()
    {
        var reminder = Create(Dentist);

        reminder.Advance(new DateTime(2026, 10, 20, 10, 0, 0), ReminderOutcome.Disabled, Now.AddDays(6));

        Assert.Equal((ReminderStatus.Completed, (DateTime?)null, (ReminderOutcome?)ReminderOutcome.Disabled),
            (reminder.Status, reminder.NextDueLocal, reminder.LastOutcome));
    }

    [Fact]
    public void Advance_AfterAnEditMeanwhile_KeepsTheUsersNewNextOccurrence()
    {
        var reminder = Create();
        reminder.Update("Take vitamins", null, ReminderSchedule.Daily(new TimeOnly(21, 0)), new DateTime(2026, 10, 15, 8, 31, 0), Now.AddDays(1));

        // The 08:30 occurrence (discovered before the edit) completes afterwards.
        Assert.False(reminder.Advance(new DateTime(2026, 10, 15, 8, 30, 0), ReminderOutcome.Notified, Now.AddDays(1)));

        Assert.Equal(new DateTime(2026, 10, 15, 21, 0, 0), reminder.NextDueLocal);
        Assert.Equal(ReminderOutcome.Notified, reminder.LastOutcome);
    }

    [Fact]
    public void Advance_OfAPausedReminder_OnlyRecordsTheOutcome()
    {
        var reminder = Create();
        reminder.Pause(Now);

        Assert.False(reminder.Advance(new DateTime(2026, 10, 15, 8, 30, 0), ReminderOutcome.Notified, Now));
        Assert.Equal((ReminderStatus.Paused, (DateTime?)new DateTime(2026, 10, 15, 8, 30, 0)), (reminder.Status, reminder.NextDueLocal));
    }

    [Fact]
    public void Advance_ABeforeTheNextOccurrence_DoesNotMoveIt()
    {
        var reminder = Create();

        Assert.False(reminder.Advance(new DateTime(2026, 10, 14, 8, 30, 0), ReminderOutcome.Notified, Now));
        Assert.Equal(new DateTime(2026, 10, 15, 8, 30, 0), reminder.NextDueLocal);
    }

    // ---- AdvanceSnooze ----

    [Fact]
    public void AdvanceSnooze_ClearsOnlyThePendingSnooze()
    {
        var reminder = Create();
        reminder.Snooze(15, Now);
        var first = reminder.SnoozeUntilUtc!.Value;

        reminder.AdvanceSnooze(first.AddMinutes(-45), ReminderOutcome.Notified, Now);   // an older snooze
        Assert.Equal(first, reminder.SnoozeUntilUtc);

        reminder.AdvanceSnooze(first, ReminderOutcome.Notified, Now.AddMinutes(16));
        Assert.Null(reminder.SnoozeUntilUtc);
        Assert.Equal(((DateTimeOffset?)Now.AddMinutes(16), (ReminderOutcome?)ReminderOutcome.Notified), (reminder.LastTriggeredAtUtc, reminder.LastOutcome));
        Assert.Equal(new DateTime(2026, 10, 15, 8, 30, 0), reminder.NextDueLocal);
    }
}
