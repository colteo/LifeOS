using LifeOS.Application.Automation;
using LifeOS.Application.Users.SetTimeZone;
using LifeOS.Domain.Assistant;

namespace LifeOS.Application.Assistant;

// AI-008: when a diary record (water intake, meal) happened, resolved on the backend (never by a model) from the
// user's explicit local date/time, the user's time zone and the current instant — the same rules as Finance's
// FinanceDraftOccurrenceResolver, to the minute (the diaries store whole minutes):
//   - no date: today in the user's zone;
//   - today without a time: the current time (truncated to the minute);
//   - any other date without a time: 12:00 local, a convention shown as such and replaceable;
//   - local → UTC with the LifeOS DST rules (LocalSchedule.ToUtc).
// Without a valid IANA time zone nothing is resolved (null): no zone is ever assumed.
public static class AssistantDiaryOccurrenceResolver
{
    public static AssistantDiaryOccurrence? Resolve(DateOnly? requestedDate, TimeOnly? requestedTime, string? timeZoneId, DateTimeOffset now)
    {
        if (!SetTimeZoneHandler.TryNormalizeIanaTimeZone(timeZoneId, out var zoneId))
        {
            return null;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var nowUtc = now.ToUniversalTime();
        var currentInstant = nowUtc.AddTicks(-(nowUtc.Ticks % TimeSpan.TicksPerMinute));
        var localNow = TimeZoneInfo.ConvertTime(currentInstant, zone);
        var today = DateOnly.FromDateTime(localNow.DateTime);

        var date = requestedDate ?? today;
        var dateSource = requestedDate is null ? AssistantDraftDateSource.Today : AssistantDraftDateSource.Explicit;

        if (requestedTime is { } time)
        {
            return AssistantDiaryOccurrence.Create(date, time, dateSource, AssistantDraftTimeSource.Explicit, zoneId,
                LocalSchedule.ToUtc(zone, date.ToDateTime(time)));
        }

        if (date == today)
        {
            return AssistantDiaryOccurrence.Create(date, TimeOnly.FromDateTime(localNow.DateTime), dateSource, AssistantDraftTimeSource.CurrentTime,
                zoneId, currentInstant);
        }

        return AssistantDiaryOccurrence.Create(date, AssistantDiaryOccurrence.Noon, dateSource, AssistantDraftTimeSource.Noon, zoneId,
            LocalSchedule.ToUtc(zone, date.ToDateTime(AssistantDiaryOccurrence.Noon)));
    }

    // The user's local today in the occurrence's zone at `now` (a diary day after it is in the future).
    public static DateOnly Today(AssistantDiaryOccurrence occurrence, DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(occurrence.TimeZoneId)).DateTime);
}

public enum AssistantDateProblem
{
    // The user has no valid time zone: "ieri" cannot be computed (no zone is ever assumed).
    TimeZoneRequired,

    // A calendar date that does not exist (31/02) or lies outside [min, max].
    InvalidDate
}

// The date the user meant, computed on the backend from the model's date expression, the user's time zone and the
// current instant — the model never supplies a date. Days are counted on the user's local calendar, so a DST change
// does not move "ieri". Shared by every module (Finance's FinanceDateExpressionResolver applies its own range).
public static class AssistantDateExpressionResolver
{
    public static (DateOnly? Date, AssistantDateProblem? Problem) Resolve(AssistantDateField field, string? timeZoneId, DateTimeOffset now,
        DateOnly min, DateOnly max)
    {
        if (!SetTimeZoneHandler.TryNormalizeIanaTimeZone(timeZoneId, out var zoneId))
        {
            return (null, AssistantDateProblem.TimeZoneRequired);
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(zoneId)).DateTime);

        DateOnly? date = field.Kind switch
        {
            AssistantDateKind.Today => today,
            AssistantDateKind.Yesterday => today.AddDays(-1),
            AssistantDateKind.DayBeforeYesterday => today.AddDays(-2),
            AssistantDateKind.DaysAgo when field.DaysAgo is { } days => today.AddDays(-days),
            AssistantDateKind.Weekday when field.Weekday is { } weekday => today.AddDays(-DaysSince(today.DayOfWeek, weekday)),
            AssistantDateKind.Calendar when field is { Day: { } day, Month: { } month } => Calendar(field.Year ?? today.Year, month, day),
            _ => null
        };

        return date is { } value && value >= min && value <= max ? (value, null) : (null, AssistantDateProblem.InvalidDate);
    }

    // 1–7: the most recent such weekday strictly before today ("lunedì scorso" on a Monday is a week ago).
    private static int DaysSince(DayOfWeek today, DayOfWeek weekday)
    {
        var days = ((int)today - (int)weekday + 7) % 7;

        return days == 0 ? 7 : days;
    }

    private static DateOnly? Calendar(int year, int month, int day) =>
        year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;
}
