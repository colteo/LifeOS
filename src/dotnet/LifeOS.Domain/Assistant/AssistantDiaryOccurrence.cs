namespace LifeOS.Domain.Assistant;

// Whether a draft's local date was stated, or is today by default.
public enum AssistantDraftDateSource
{
    Explicit,
    Today
}

// Whether a draft's local time was stated, or is a convention: the current time for today, 12:00 for any other
// date. Conventions are shown in the draft and can be replaced by an explicit time.
public enum AssistantDraftTimeSource
{
    Explicit,
    CurrentTime,
    Noon
}

// The field names shared by the diary drafts (water intakes, meals): also the HTTP field names.
public static class AssistantDiaryDraftFields
{
    public const string OccurredOn = "occurredOn";
    public const string OccurredTime = "occurredTime";

    // The resolved instant: missing until a date can be resolved in the user's valid time zone.
    public const string OccurredAt = "occurredAt";
}

// AI-008: when a diary record (a water intake, a meal) happened, as its draft shows it: the user's local date and
// time, how each was determined, the time zone they were read in, and the resulting UTC instant (computed by the
// Application layer with the LifeOS DST rules). The diaries store whole minutes and a local date + UTC offset, so
// the time is a whole minute and the offset (local − UTC) is derived from the instant. Fixed once resolved: the
// draft shows exactly what a confirmation will record. (Finance keeps its own FinanceDraftOccurrence, to the
// second.)
public sealed record AssistantDiaryOccurrence
{
    public const int MaxTimeZoneIdLength = 64;

    // The widest real-world UTC offsets, as the diaries accept them.
    public const int MaxUtcOffsetMinutes = 14 * 60;

    public static readonly TimeOnly Noon = new(12, 0);

    public static readonly DateOnly MinDate = new(1900, 1, 1);
    public static readonly DateOnly MaxDate = new(2999, 12, 31);

    private AssistantDiaryOccurrence() { }

    public DateOnly LocalDate { get; private init; }

    public TimeOnly LocalTime { get; private init; }

    public AssistantDraftDateSource DateSource { get; private init; }

    public AssistantDraftTimeSource TimeSource { get; private init; }

    public string TimeZoneId { get; private init; } = "";

    public DateTimeOffset OccurredAtUtc { get; private init; }

    // Local wall-clock time minus the instant, in minutes (what the diary commands take).
    public int UtcOffsetMinutes => (int)(LocalDate.ToDateTime(LocalTime) - OccurredAtUtc.UtcDateTime).TotalMinutes;

    public static AssistantDiaryOccurrence Create(
        DateOnly localDate,
        TimeOnly localTime,
        AssistantDraftDateSource dateSource,
        AssistantDraftTimeSource timeSource,
        string timeZoneId,
        DateTimeOffset occurredAtUtc)
    {
        if (!Enum.IsDefined(dateSource) || !Enum.IsDefined(timeSource))
        {
            throw new ArgumentException("Unknown date or time source.", AssistantDiaryDraftFields.OccurredAt);
        }

        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > MaxTimeZoneIdLength)
        {
            throw new ArgumentException("A time zone id is required.", AssistantDiaryDraftFields.OccurredAt);
        }

        EnsureDateInRange(localDate, AssistantDiaryDraftFields.OccurredOn);
        EnsureWholeMinute(localTime, AssistantDiaryDraftFields.OccurredTime);

        if (timeSource == AssistantDraftTimeSource.Noon && localTime != Noon)
        {
            throw new ArgumentException("The conventional time is 12:00.", AssistantDiaryDraftFields.OccurredTime);
        }

        var instant = occurredAtUtc.ToUniversalTime();

        if (instant.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentException("The instant must be a whole minute.", AssistantDiaryDraftFields.OccurredAt);
        }

        var occurrence = new AssistantDiaryOccurrence
        {
            LocalDate = localDate,
            LocalTime = localTime,
            DateSource = dateSource,
            TimeSource = timeSource,
            TimeZoneId = timeZoneId,
            OccurredAtUtc = instant
        };

        if (Math.Abs(occurrence.UtcOffsetMinutes) > MaxUtcOffsetMinutes)
        {
            throw new ArgumentException("The UTC offset must be between -14:00 and +14:00.", AssistantDiaryDraftFields.OccurredAt);
        }

        return occurrence;
    }

    public static void EnsureDateInRange(DateOnly date, string field)
    {
        if (date < MinDate || date > MaxDate)
        {
            throw new ArgumentOutOfRangeException(field, date, $"The date must be between {MinDate:yyyy-MM-dd} and {MaxDate:yyyy-MM-dd}.");
        }
    }

    public static void EnsureWholeMinute(TimeOnly time, string field)
    {
        if (time.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentException("The time must be a whole minute.", field);
        }
    }

    // An explicit date/time is the occurrence's; a missing one is a convention (today; current time or noon).
    public static void EnsureConsistent(AssistantDiaryOccurrence occurrence, DateOnly? requestedDate, TimeOnly? requestedTime)
    {
        var dateMatches = requestedDate is { } date
            ? occurrence.DateSource == AssistantDraftDateSource.Explicit && occurrence.LocalDate == date
            : occurrence.DateSource == AssistantDraftDateSource.Today;

        var timeMatches = requestedTime is { } time
            ? occurrence.TimeSource == AssistantDraftTimeSource.Explicit && occurrence.LocalTime == time
            : occurrence.TimeSource != AssistantDraftTimeSource.Explicit;

        if (!dateMatches || !timeMatches)
        {
            throw new ArgumentException("The resolved date and time do not match the requested ones.", AssistantDiaryDraftFields.OccurredAt);
        }
    }
}
