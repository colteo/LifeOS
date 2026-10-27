using LifeOS.Domain.Assistant;

namespace LifeOS.Domain.Nutrition.Hydration.Assistant;

// The field names the draft reports as missing or invalid (also the HTTP field names).
public static class HydrationDraftFields
{
    public const string AmountMl = "amountMl";
    public const string OccurredOn = AssistantDiaryDraftFields.OccurredOn;
    public const string OccurredTime = AssistantDiaryDraftFields.OccurredTime;
    public const string OccurredAt = AssistantDiaryDraftFields.OccurredAt;
}

// AI-008: the typed draft of one water intake (kind hydration.intake.create). Every field may be missing while
// the user is still completing it; what is present is always valid in shape:
//   - amount: whole millilitres within the intake's limits (HydrationIntake.MinAmountMl..MaxAmountMl), always a
//     quantity the user stated (ml, cl or litres, converted deterministically by the Application layer; a
//     "glass" is never converted);
//   - requested date/time: what the user explicitly stated (whole minutes); the occurrence: the resolved instant.
// Immutable, equality by value (a revision equal to the draft changes nothing).
public sealed record HydrationIntakeDraft : IAssistantDraft
{
    // The schema version of the stored draft document (AssistantOperation.PayloadVersion).
    public const int PayloadVersion = 1;

    private HydrationIntakeDraft() { }

    public int? AmountMl { get; private init; }

    public DateOnly? RequestedDate { get; private init; }

    public TimeOnly? RequestedTime { get; private init; }

    // Null while no valid time zone was available to resolve it.
    public AssistantDiaryOccurrence? Occurrence { get; private init; }

    public static HydrationIntakeDraft Empty { get; } = new();

    // The fields a confirmation still needs, in display order.
    public IReadOnlyList<string> MissingFields
    {
        get
        {
            var missing = new List<string>(2);

            if (AmountMl is null) missing.Add(HydrationDraftFields.AmountMl);
            if (Occurrence is null) missing.Add(HydrationDraftFields.OccurredAt);

            return missing;
        }
    }

    public bool IsComplete => AmountMl is not null && Occurrence is not null;

    // Validates every field (ArgumentException with the field name as ParamName).
    public static HydrationIntakeDraft Create(int? amountMl, DateOnly? requestedDate, TimeOnly? requestedTime, AssistantDiaryOccurrence? occurrence)
    {
        if (amountMl is < HydrationIntake.MinAmountMl or > HydrationIntake.MaxAmountMl)
        {
            throw new ArgumentOutOfRangeException(HydrationDraftFields.AmountMl, amountMl,
                $"Enter an amount between {HydrationIntake.MinAmountMl} and {HydrationIntake.MaxAmountMl} ml.");
        }

        if (requestedDate is { } date)
        {
            AssistantDiaryOccurrence.EnsureDateInRange(date, HydrationDraftFields.OccurredOn);
        }

        if (requestedTime is { } time)
        {
            AssistantDiaryOccurrence.EnsureWholeMinute(time, HydrationDraftFields.OccurredTime);
        }

        if (occurrence is not null)
        {
            AssistantDiaryOccurrence.EnsureConsistent(occurrence, requestedDate, requestedTime);
        }

        return new HydrationIntakeDraft
        {
            AmountMl = amountMl,
            RequestedDate = requestedDate,
            RequestedTime = requestedTime,
            Occurrence = occurrence
        };
    }
}
