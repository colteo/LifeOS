namespace LifeOS.Domain.Habits;

// A yes/no recurring behaviour of one user (HAB-001 §5).
//
// Schedule: one or more versions, the first starting on StartsOn, strictly increasing by EffectiveFrom,
// consecutive identical versions merged. Frequency changes, archive and restore are versioned from the
// caller's local "today": a version starting today is replaced, otherwise a new one starts today, and a
// past version is never changed. So past days always keep the frequency they had.
//
// "today" is always the caller's local calendar date (Application: UTC now + utcOffsetMinutes).
// Timestamps are UTC truncated to whole microseconds, the precision PostgreSQL stores.
public sealed class Habit
{
    public const int NameMaxLength = 80;
    public const int DescriptionMaxLength = 500;

    // StartsOn must be within this many days of today at creation.
    public const int MaxStartOffsetDays = 365;

    private readonly List<HabitScheduleVersion> _schedule = [];

    private Habit() { }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Name { get; private set; } = "";

    // Null when the habit has no description.
    public string? Description { get; private set; }

    public HabitCompletionSource CompletionSource { get; private set; }

    // Immutable in v1.
    public DateOnly StartsOn { get; private set; }

    public HabitStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public DateTimeOffset? ArchivedAtUtc { get; private set; }

    // Ordered by EffectiveFrom; the first starts on StartsOn.
    public IReadOnlyList<HabitScheduleVersion> Schedule => _schedule.OrderBy(version => version.EffectiveFrom).ToList();

    // The latest version's frequency (Inactive while archived).
    public HabitFrequency CurrentFrequency => Schedule[^1].Frequency;

    // The latest frequency that is not Inactive, if any.
    public HabitFrequency? LastActiveFrequency =>
        Schedule.LastOrDefault(version => version.Kind != HabitFrequencyKind.Inactive)?.Frequency;

    public static Habit Create(Guid userId, string name, string? description, HabitCompletionSource completionSource,
        HabitFrequency frequency, DateOnly startsOn, DateOnly today, DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        ArgumentNullException.ThrowIfNull(frequency);

        if (!Enum.IsDefined(completionSource))
        {
            throw new ArgumentOutOfRangeException(nameof(completionSource), "Unknown completion source.");
        }

        if (frequency.Kind == HabitFrequencyKind.Inactive)
        {
            throw new ArgumentException("Choose how often the habit is expected.", nameof(frequency));
        }

        if (startsOn < today.AddDays(-MaxStartOffsetDays) || startsOn > today.AddDays(MaxStartOffsetDays))
        {
            throw new ArgumentOutOfRangeException(nameof(startsOn), $"Choose a start date within {MaxStartOffsetDays} days of today.");
        }

        var habit = new Habit
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            CompletionSource = completionSource,
            StartsOn = startsOn,
            Status = HabitStatus.Active,
            CreatedAtUtc = ToStoredPrecision(now)
        };

        habit.ApplyDetails(name, description, now);
        habit._schedule.Add(new HabitScheduleVersion(habit.Id, userId, startsOn, frequency));

        return habit;
    }

    // Name and description can change at any time, also while archived.
    public void UpdateDetails(string name, string? description, DateTimeOffset now) => ApplyDetails(name, description, now);

    // The version on a date; null before StartsOn.
    public HabitScheduleVersion? VersionOn(DateOnly date) =>
        Schedule.LastOrDefault(version => version.EffectiveFrom <= date);

    // Applies from today; past days keep their schedule. Returns false when nothing changed.
    public bool ChangeFrequency(HabitFrequency frequency, DateOnly today, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(frequency);

        if (frequency.Kind == HabitFrequencyKind.Inactive)
        {
            throw new ArgumentException("Choose how often the habit is expected.", nameof(frequency));
        }

        if (Status == HabitStatus.Archived)
        {
            throw new HabitConflictException(HabitConflictException.Archived, "Restore the habit before changing how often it is expected.");
        }

        return SetFrom(today, frequency, now);
    }

    // Hidden from Today and not scheduled from today on; history is kept. Idempotent.
    public void Archive(DateOnly today, DateTimeOffset now)
    {
        if (Status == HabitStatus.Archived)
        {
            return;
        }

        SetFrom(today, HabitFrequency.Inactive, now);
        Status = HabitStatus.Archived;
        ArchivedAtUtc = ToStoredPrecision(now);
        UpdatedAtUtc = ToStoredPrecision(now);
    }

    // Scheduled again from today with the given frequency, by default the last one it had (Daily when
    // it never had another, e.g. archived on its first day). Idempotent.
    public void Restore(DateOnly today, HabitFrequency? frequency, DateTimeOffset now)
    {
        if (Status == HabitStatus.Active)
        {
            return;
        }

        var restored = frequency ?? LastActiveFrequency ?? HabitFrequency.Daily;

        if (restored.Kind == HabitFrequencyKind.Inactive)
        {
            throw new ArgumentException("Choose how often the habit is expected.", nameof(frequency));
        }

        SetFrom(today, restored, now);
        Status = HabitStatus.Active;
        ArchivedAtUtc = null;
        UpdatedAtUtc = ToStoredPrecision(now);
    }

    // Validates a Done/Excused status for a local date (HAB-001 §5.1 rules 7-10) and returns the
    // check-in to store. ArgumentException: invalid input (400). HabitConflictException: the habit's
    // state forbids it (409).
    public HabitCheckIn CheckIn(DateOnly date, HabitCheckInStatus status, string? note, DateOnly today, DateTimeOffset now)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Choose Done or Excused.");
        }

        ValidateCheckInDate(date, today);

        var version = VersionOn(date)!;

        if (version.Kind == HabitFrequencyKind.Inactive)
        {
            throw new HabitConflictException(HabitConflictException.InactiveOnDate, "The habit was archived on this date.");
        }

        if (status == HabitCheckInStatus.Done && CompletionSource != HabitCompletionSource.Manual)
        {
            throw new HabitConflictException(HabitConflictException.CompletionDerived, "This habit is completed automatically.");
        }

        var frequency = version.Frequency;

        if (status == HabitCheckInStatus.Excused && frequency.IsDayBased && !frequency.IsScheduledOn(date))
        {
            throw new ArgumentException("This day is not scheduled for the habit, so there is nothing to excuse.", nameof(status));
        }

        return new HabitCheckIn(Id, UserId, date, status, HabitCheckIn.NormalizeNote(note), ToStoredPrecision(now));
    }

    // A check-in date is between StartsOn and today.
    public void ValidateCheckInDate(DateOnly date, DateOnly today)
    {
        if (date > today)
        {
            throw new ArgumentOutOfRangeException(nameof(date), "You can't check in for a future date.");
        }

        if (date < StartsOn)
        {
            throw new ArgumentOutOfRangeException(nameof(date), "This date is before the habit starts.");
        }
    }

    // UTC, truncated to whole microseconds.
    public static DateTimeOffset ToStoredPrecision(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();

        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    // Versions only ever start at or after the latest one (a device whose clock or zone moves back cannot
    // rewrite a version that already started), and never before StartsOn.
    private bool SetFrom(DateOnly today, HabitFrequency frequency, DateTimeOffset now)
    {
        var ordered = Schedule;
        var latest = ordered[^1];
        var from = new[] { today, StartsOn, latest.EffectiveFrom }.Max();

        if (latest.EffectiveFrom == from)
        {
            var previous = ordered.Count > 1 ? ordered[^2] : null;

            if (latest.Frequency == frequency)
            {
                return false;
            }

            // Back to the previous frequency on the same day: the two versions merge.
            if (previous is not null && previous.Frequency == frequency)
            {
                _schedule.Remove(latest);
            }
            else
            {
                latest.Apply(frequency);
            }
        }
        else
        {
            if (latest.Frequency == frequency)
            {
                return false;
            }

            _schedule.Add(new HabitScheduleVersion(Id, UserId, from, frequency));
        }

        UpdatedAtUtc = ToStoredPrecision(now);
        return true;
    }

    private void ApplyDetails(string name, string? description, DateTimeOffset now)
    {
        var normalizedName = NormalizeName(name);
        var normalizedDescription = NormalizeDescription(description);

        Name = normalizedName;
        Description = normalizedDescription;
        UpdatedAtUtc = ToStoredPrecision(now);
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Give the habit a name.", nameof(name));
        }

        var text = name.Trim();

        if (text.Length > NameMaxLength)
        {
            throw new ArgumentException($"The name must be at most {NameMaxLength} characters.", nameof(name));
        }

        return text;
    }

    // Blank is "no description". Longer text is rejected, never cut.
    private static string? NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var text = description.Trim();

        if (text.Length > DescriptionMaxLength)
        {
            throw new ArgumentException($"The description must be at most {DescriptionMaxLength} characters.", nameof(description));
        }

        return text;
    }
}
