namespace LifeOS.Domain.Nutrition;

// One whole meal in the user's journal, as free text (NUT-001). Nothing is parsed or split.
//
// Time model: DiaryDate and DiaryTime are the local calendar day and wall-clock time the user
// recorded; they decide grouping and order, so a later device time-zone change never moves a meal to
// another day. OccurredAtUtc is the real instant, derived from them and the UTC offset at recording.
// The offset is not stored separately: it is always (DiaryDate + DiaryTime) - OccurredAtUtc.
public sealed class MealEntry
{
    public const int DescriptionMaxLength = 2000;

    // The widest real-world UTC offsets are -12:00 and +14:00; ±14 h is accepted.
    public const int MaxUtcOffsetMinutes = 14 * 60;

    private MealEntry() { }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public string Description { get; private set; } = "";

    public MealType? MealType { get; private set; }

    public DateOnly DiaryDate { get; private set; }

    public TimeOnly DiaryTime { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    // The offset the meal was recorded with, in minutes east of UTC.
    public int UtcOffsetMinutes => (int)(DiaryDate.ToDateTime(DiaryTime) - OccurredAtUtc.UtcDateTime).TotalMinutes;

    public static MealEntry Create(Guid userId, string description, MealType? mealType, DateOnly diaryDate,
        TimeOnly time, int utcOffsetMinutes, DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        // Npgsql represents the DateOnly endpoints as PostgreSQL infinities, not calendar dates.
        if (diaryDate == DateOnly.MinValue || diaryDate == DateOnly.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(diaryDate), "Choose a calendar date between 0001-01-02 and 9999-12-30.");
        }

        if (utcOffsetMinutes is < -MaxUtcOffsetMinutes or > MaxUtcOffsetMinutes)
        {
            throw new ArgumentOutOfRangeException(nameof(utcOffsetMinutes), "UTC offset must be between -840 and 840 minutes.");
        }

        var entry = new MealEntry
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            DiaryDate = diaryDate,
            CreatedAtUtc = now.ToUniversalTime()
        };

        entry.Apply(description, mealType, time, utcOffsetMinutes, now);

        return entry;
    }

    // The meal stays on its diary day and keeps the UTC offset it was recorded with.
    public void Update(string description, MealType? mealType, TimeOnly time, DateTimeOffset now) =>
        Apply(description, mealType, time, UtcOffsetMinutes, now);

    private void Apply(string description, MealType? mealType, TimeOnly time, int utcOffsetMinutes, DateTimeOffset now)
    {
        var text = NormalizeDescription(description);

        if (mealType is { } type && !Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(mealType), "Unknown meal type.");
        }

        if (time.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentException("Time must be a whole minute.", nameof(time));
        }

        Description = text;
        MealType = mealType;
        DiaryTime = time;
        OccurredAtUtc = new DateTimeOffset(DiaryDate.ToDateTime(time), TimeSpan.FromMinutes(utcOffsetMinutes)).ToUniversalTime();
        UpdatedAtUtc = now.ToUniversalTime();
    }

    // Outer whitespace is trimmed; internal text and line breaks are kept.
    private static string NormalizeDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Describe what you ate.", nameof(description));
        }

        var text = description.Trim();

        if (text.Length > DescriptionMaxLength)
        {
            throw new ArgumentException($"Description must be at most {DescriptionMaxLength} characters.", nameof(description));
        }

        return text;
    }
}
