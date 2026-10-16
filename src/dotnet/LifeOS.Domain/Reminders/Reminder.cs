namespace LifeOS.Domain.Reminders;

// A user-owned reminder (REM-001 §5). The user's title and note are shown only inside the app: a push
// carries fixed copy and the reminder's id (D-8).
//
// "localNow" is always the caller's local wall-clock time (Application: UTC now + the device's offset,
// or the user's IANA zone in the automation), truncated to the minute. NextDueLocal is the next
// occurrence as local wall-clock time; it does not depend on the zone, so a zone change needs no
// rewrite (D-3). Timestamps are UTC truncated to whole microseconds, the precision PostgreSQL stores.
//
// Invariants: Active ⇔ NextDueLocal set (strictly after the localNow it was computed from); Completed ⇒
// NextDueLocal null; Paused keeps its fields and is never discovered.
//
// Kinds (R5): Custom has a title (and optional note); the conditional kinds HabitIncomplete (one habit,
// HabitId) and HydrationGoal have neither, repeat daily or weekly (never Once, so they never complete),
// and their condition is evaluated by the Application when an occurrence runs and before each send.
public sealed class Reminder
{
    public const int TitleMaxLength = 80;
    public const int NoteMaxLength = 500;

    // D-11: the snooze choices, in minutes.
    public static readonly IReadOnlyList<int> SnoozeMinutes = [15, 60, 180];

    private Reminder() { }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public ReminderKind Kind { get; private set; }

    // Custom only (null for the conditional kinds).
    public string? Title { get; private set; }

    // Null when the reminder has no note (always for the conditional kinds).
    public string? Note { get; private set; }

    // HabitIncomplete only: the habit it nudges about (deleting the habit deletes the reminder).
    public Guid? HabitId { get; private set; }

    public bool IsConditional => Kind != ReminderKind.Custom;

    // The schedule's stored parts (ReminderSchedule.FromParts); Schedule is the value object.
    public ReminderScheduleKind ScheduleKind { get; private set; }

    public DateOnly? OnceDate { get; private set; }

    public int? WeekdayMask { get; private set; }

    public TimeOnly LocalTime { get; private set; }

    public ReminderSchedule Schedule => ReminderSchedule.FromParts(ScheduleKind, OnceDate, WeekdayMask, LocalTime);

    public ReminderStatus Status { get; private set; }

    public DateTime? NextDueLocal { get; private set; }

    // One pending snooze: the UTC instant (whole minutes) at which the reminder fires again.
    public DateTimeOffset? SnoozeUntilUtc { get; private set; }

    public DateTimeOffset? LastTriggeredAtUtc { get; private set; }

    public ReminderOutcome? LastOutcome { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static Reminder Create(Guid userId, string title, string? note, ReminderSchedule schedule, DateTime localNow, DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        var reminder = new Reminder
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Kind = ReminderKind.Custom,
            Status = ReminderStatus.Active,
            CreatedAtUtc = ToStoredPrecision(now)
        };

        reminder.Apply(title, note, schedule, localNow, now);

        return reminder;
    }

    // R5: a nudge sent only while its condition holds (habitId: HabitIncomplete only). Daily or weekly.
    public static Reminder CreateConditional(Guid userId, ReminderKind kind, Guid? habitId, ReminderSchedule schedule, DateTime localNow,
        DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        if (!Enum.IsDefined(kind) || kind == ReminderKind.Custom)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "Choose a habit or the water target.");
        }

        if (kind == ReminderKind.HabitIncomplete ? habitId is null || habitId == Guid.Empty : habitId is not null)
        {
            throw new ArgumentException(kind == ReminderKind.HabitIncomplete ? "Choose the habit." : "Only a habit reminder has a habit.",
                nameof(habitId));
        }

        var reminder = new Reminder
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Kind = kind,
            HabitId = habitId,
            Status = ReminderStatus.Active,
            CreatedAtUtc = ToStoredPrecision(now)
        };

        reminder.Apply(null, null, schedule, localNow, now);

        return reminder;
    }

    // Title, note and schedule (the schedule only for the conditional kinds: their title and note are
    // ignored); the next occurrence is recomputed from now and a pending snooze is dropped. A Paused
    // reminder stays paused; a Completed one becomes Active again (e.g. a new date).
    public void Update(string? title, string? note, ReminderSchedule schedule, DateTime localNow, DateTimeOffset now)
    {
        Apply(IsConditional ? null : title, IsConditional ? null : note, schedule, localNow, now);

        if (Status == ReminderStatus.Completed)
        {
            Status = ReminderStatus.Active;
        }
    }

    // No pushes until resumed; a pending snooze is dropped. Idempotent.
    public void Pause(DateTimeOffset now)
    {
        if (Status == ReminderStatus.Completed)
        {
            throw new ReminderConflictException(ReminderConflictException.Completed, "This reminder is already completed.");
        }

        if (Status == ReminderStatus.Paused)
        {
            return;
        }

        Status = ReminderStatus.Paused;
        SnoozeUntilUtc = null;
        UpdatedAtUtc = ToStoredPrecision(now);
    }

    // The next occurrence after now: missed occurrences are not caught up (REM-US4). A one-time reminder
    // whose time passed while paused is completed as Missed. Idempotent for an Active reminder.
    public void Resume(DateTime localNow, DateTimeOffset now)
    {
        if (Status == ReminderStatus.Completed)
        {
            throw new ReminderConflictException(ReminderConflictException.Completed, "A completed reminder can't be resumed. Edit it to choose a new time.");
        }

        if (Status == ReminderStatus.Active)
        {
            return;
        }

        NextDueLocal = Schedule.FirstAfter(ReminderSchedule.ToMinute(localNow));

        if (NextDueLocal is null)
        {
            Status = ReminderStatus.Completed;
            LastOutcome = ReminderOutcome.Missed;
        }
        else
        {
            Status = ReminderStatus.Active;
        }

        UpdatedAtUtc = ToStoredPrecision(now);
    }

    // D-11: fire again in 15 min, 1 h or 3 h (whole minutes), replacing a pending snooze. Allowed for an
    // Active or Completed reminder; the regular schedule is unchanged.
    public void Snooze(int minutes, DateTimeOffset now)
    {
        if (!SnoozeMinutes.Contains(minutes))
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), "Snooze for 15 minutes, 1 hour or 3 hours.");
        }

        if (Status == ReminderStatus.Paused)
        {
            throw new ReminderConflictException(ReminderConflictException.Paused, "Resume the reminder before snoozing it.");
        }

        var utc = now.ToUniversalTime();

        SnoozeUntilUtc = new DateTimeOffset(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, TimeSpan.Zero).AddMinutes(minutes);
        UpdatedAtUtc = ToStoredPrecision(now);
    }

    // §5 rule 6: the occurrence at occurrenceLocal ran with this outcome. When it is (or is past) the
    // pending occurrence, the next one after it becomes due; a one-time reminder with no further
    // occurrence becomes Completed. If the reminder was edited, paused or advanced meanwhile its
    // NextDueLocal is already after the occurrence and only the Last* fields change. Returns whether
    // the schedule moved.
    public bool Advance(DateTime occurrenceLocal, ReminderOutcome outcome, DateTimeOffset now)
    {
        RequireOutcome(outcome);

        var moved = false;

        if (Status == ReminderStatus.Active && NextDueLocal is { } next && next <= occurrenceLocal)
        {
            NextDueLocal = Schedule.FirstAfter(occurrenceLocal);
            Status = NextDueLocal is null ? ReminderStatus.Completed : ReminderStatus.Active;
            moved = true;
        }

        Record(outcome, now);
        return moved;
    }

    // §5 rule 7: the snooze at snoozeUtc ran. It is cleared only if it is still the pending one.
    public void AdvanceSnooze(DateTimeOffset snoozeUtc, ReminderOutcome outcome, DateTimeOffset now)
    {
        RequireOutcome(outcome);

        if (SnoozeUntilUtc == snoozeUtc)
        {
            SnoozeUntilUtc = null;
        }

        Record(outcome, now);
    }

    // UTC, truncated to whole microseconds.
    public static DateTimeOffset ToStoredPrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();

        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    private void Apply(string? title, string? note, ReminderSchedule schedule, DateTime localNow, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        if (IsConditional && schedule.Kind == ReminderScheduleKind.Once)
        {
            throw new ArgumentException("A habit or water reminder repeats every day or on some days.", "schedule.kind");
        }

        var normalizedTitle = IsConditional ? null : NormalizeTitle(title ?? "");
        var normalizedNote = IsConditional ? null : NormalizeNote(note);
        var next = schedule.FirstAfter(ReminderSchedule.ToMinute(localNow))
            ?? throw new ArgumentException("Choose a date and time in the future.", "schedule.date");

        Title = normalizedTitle;
        Note = normalizedNote;
        ScheduleKind = schedule.Kind;
        OnceDate = schedule.Date;
        WeekdayMask = schedule.WeekdayMask;
        LocalTime = schedule.Time;
        NextDueLocal = next;
        SnoozeUntilUtc = null;
        UpdatedAtUtc = ToStoredPrecision(now);
    }

    private void Record(ReminderOutcome outcome, DateTimeOffset now)
    {
        LastTriggeredAtUtc = ToStoredPrecision(now);
        LastOutcome = outcome;
        UpdatedAtUtc = ToStoredPrecision(now);
    }

    private static void RequireOutcome(ReminderOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    private static string NormalizeTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Say what to remind you about.", nameof(title));
        }

        var text = title.Trim();

        if (text.Length > TitleMaxLength)
        {
            throw new ArgumentException($"The title must be at most {TitleMaxLength} characters.", nameof(title));
        }

        return text;
    }

    // Blank is "no note". Longer text is rejected, never cut.
    private static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var text = note.Trim();

        if (text.Length > NoteMaxLength)
        {
            throw new ArgumentException($"The note must be at most {NoteMaxLength} characters.", nameof(note));
        }

        return text;
    }
}
