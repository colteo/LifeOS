using LifeOS.Domain.Assistant;

namespace LifeOS.Application.Assistant;

// AI-008: what the diary modules (Hydration, Meals) share when they apply a turn: the issue codes a reply asks
// about, and the date/time of the record, computed on the backend from the model's expressions in the user's time
// zone (never a date from the model).
public static class AssistantDiaryIssues
{
    // The model marked the field ambiguous (Field = the protocol field name).
    public const string FieldAmbiguous = "field_ambiguous";

    public const string DateInvalid = "date_invalid";

    public const string TimeZoneRequired = "time_zone_required";
}

// A problem found while resolving a turn, to be asked back instead of applied.
public sealed record AssistantDiaryIssue(string Code, string? Field = null);

// The date and time a turn sets or clears on a diary draft.
public sealed class AssistantDiaryWhen
{
    public DateOnly? OccurredOn { get; private set; }

    public TimeOnly? OccurredTime { get; private set; }

    public bool ClearDate { get; private set; }

    public bool ClearTime { get; private set; }

    // `usable` is false for an Absent field or one the model marked ambiguous (already reported by the caller).
    public static AssistantDiaryWhen Plan(AssistantDateField date, bool dateUsable, AssistantTimeField time, bool timeUsable, string? timeZoneId,
        DateTimeOffset now, Action<AssistantDiaryIssue> report)
    {
        var when = new AssistantDiaryWhen();

        if (dateUsable)
        {
            if (date.State == AssistantFieldState.Clear)
            {
                when.ClearDate = true;
            }
            else
            {
                var (resolved, problem) = AssistantDateExpressionResolver.Resolve(date, timeZoneId, now, AssistantDiaryOccurrence.MinDate,
                    AssistantDiaryOccurrence.MaxDate);

                if (resolved is not null)
                {
                    when.OccurredOn = resolved;
                }
                else
                {
                    report(new AssistantDiaryIssue(problem == AssistantDateProblem.TimeZoneRequired
                        ? AssistantDiaryIssues.TimeZoneRequired
                        : AssistantDiaryIssues.DateInvalid));
                }
            }
        }

        if (timeUsable)
        {
            if (time.State == AssistantFieldState.Clear)
            {
                when.ClearTime = true;
            }
            else
            {
                when.OccurredTime = new TimeOnly(time.Hour!.Value, time.Minute!.Value);
            }
        }

        return when;
    }
}
