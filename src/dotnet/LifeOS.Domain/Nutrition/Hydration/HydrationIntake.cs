namespace LifeOS.Domain.Nutrition.Hydration;

// One drink of water (HYD-001 P-1), in whole millilitres. Hydration is the single source of truth for
// water: totals are always computed from these rows and never stored, here or in another module.
//
// Time model (as NUT-001 meals): DiaryDate and DiaryTime are the local calendar day and wall-clock time
// the user recorded; they decide grouping and order, so a later device time-zone change never moves an
// intake to another day. OccurredAtUtc is the real instant, derived from them and the UTC offset at
// recording. The offset is not stored separately: it is always (DiaryDate + DiaryTime) - OccurredAtUtc.
public sealed class HydrationIntake
{
    public const int MinAmountMl = 1;

    // Covers any real container (a 3 l jug) and still catches a typo like 25000 (P-2). No daily cap.
    public const int MaxAmountMl = 4000;

    // The widest real-world UTC offsets are -12:00 and +14:00; ±14 h is accepted.
    public const int MaxUtcOffsetMinutes = 14 * 60;

    private HydrationIntake() { }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public int AmountMl { get; private set; }

    public DateOnly DiaryDate { get; private set; }

    public TimeOnly DiaryTime { get; private set; }

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    // The offset the intake was recorded with, in minutes east of UTC.
    public int UtcOffsetMinutes => (int)(DiaryDate.ToDateTime(DiaryTime) - OccurredAtUtc.UtcDateTime).TotalMinutes;

    public static HydrationIntake Create(Guid userId, int amountMl, DateOnly date, TimeOnly time, int utcOffsetMinutes, DateTimeOffset now)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        // Npgsql represents the DateOnly endpoints as PostgreSQL infinities, not calendar dates.
        if (date == DateOnly.MinValue || date == DateOnly.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(date), "Choose a calendar date between 0001-01-02 and 9999-12-30.");
        }

        if (utcOffsetMinutes is < -MaxUtcOffsetMinutes or > MaxUtcOffsetMinutes)
        {
            throw new ArgumentOutOfRangeException(nameof(utcOffsetMinutes), "UTC offset must be between -840 and 840 minutes.");
        }

        var intake = new HydrationIntake
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            DiaryDate = date,
            CreatedAtUtc = now.ToUniversalTime()
        };

        intake.Apply(amountMl, time, utcOffsetMinutes, now);

        return intake;
    }

    // The intake stays on its diary day and keeps the UTC offset it was recorded with (P-8).
    public void Update(int amountMl, TimeOnly time, DateTimeOffset now) =>
        Apply(amountMl, time, UtcOffsetMinutes, now);

    private void Apply(int amountMl, TimeOnly time, int utcOffsetMinutes, DateTimeOffset now)
    {
        if (amountMl is < MinAmountMl or > MaxAmountMl)
        {
            throw new ArgumentOutOfRangeException(nameof(amountMl), $"Enter an amount between {MinAmountMl} and {MaxAmountMl} ml.");
        }

        if (time.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentException("Time must be a whole minute.", nameof(time));
        }

        AmountMl = amountMl;
        DiaryTime = time;
        OccurredAtUtc = new DateTimeOffset(DiaryDate.ToDateTime(time), TimeSpan.FromMinutes(utcOffsetMinutes)).ToUniversalTime();
        UpdatedAtUtc = now.ToUniversalTime();
    }
}
