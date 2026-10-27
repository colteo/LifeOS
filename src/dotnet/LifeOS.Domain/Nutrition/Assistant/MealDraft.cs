using LifeOS.Domain.Assistant;

namespace LifeOS.Domain.Nutrition.Assistant;

// The field names the draft reports as missing or invalid (also the HTTP field names).
public static class MealDraftFields
{
    public const string Description = "description";
    public const string MealType = "mealType";
    public const string OccurredOn = AssistantDiaryDraftFields.OccurredOn;
    public const string OccurredTime = AssistantDiaryDraftFields.OccurredTime;
    public const string OccurredAt = AssistantDiaryDraftFields.OccurredAt;
}

// AI-008: the typed draft of one meal of the text meal journal (kind nutrition.meal.create). Every field may be
// missing while the user is still completing it; what is present is always valid in shape:
//   - description: the user's own words (never ingredients, quantities or nutrition the user did not write),
//     trimmed, at most MaxDescriptionLength characters, no NUL;
//   - meal type: optional (the journal does not need one), only when stated;
//   - requested date/time: what the user explicitly stated (whole minutes); the occurrence: the resolved instant.
// Nutrition is never part of the draft: estimating it stays an explicit, separate action of the meal journal.
// Immutable, equality by value (a revision equal to the draft changes nothing).
public sealed record MealDraft : IAssistantDraft
{
    // A draft never holds more text than an assistant message could carry (MealEntry allows longer texts).
    public const int MaxDescriptionLength = AssistantMessage.MaxUserContentLength;

    // The schema version of the stored draft document (AssistantOperation.PayloadVersion).
    public const int PayloadVersion = 1;

    private MealDraft() { }

    public string? Description { get; private init; }

    public MealType? MealType { get; private init; }

    public DateOnly? RequestedDate { get; private init; }

    public TimeOnly? RequestedTime { get; private init; }

    // Null while no valid time zone was available to resolve it.
    public AssistantDiaryOccurrence? Occurrence { get; private init; }

    public static MealDraft Empty { get; } = new();

    // The fields a confirmation still needs, in display order.
    public IReadOnlyList<string> MissingFields
    {
        get
        {
            var missing = new List<string>(2);

            if (Description is null) missing.Add(MealDraftFields.Description);
            if (Occurrence is null) missing.Add(MealDraftFields.OccurredAt);

            return missing;
        }
    }

    public bool IsComplete => Description is not null && Occurrence is not null;

    // Validates and normalises every field (ArgumentException with the field name as ParamName).
    public static MealDraft Create(string? description, MealType? mealType, DateOnly? requestedDate, TimeOnly? requestedTime,
        AssistantDiaryOccurrence? occurrence)
    {
        if (mealType is { } type && !Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(MealDraftFields.MealType, type, "Unknown meal type.");
        }

        if (requestedDate is { } date)
        {
            AssistantDiaryOccurrence.EnsureDateInRange(date, MealDraftFields.OccurredOn);
        }

        if (requestedTime is { } time)
        {
            AssistantDiaryOccurrence.EnsureWholeMinute(time, MealDraftFields.OccurredTime);
        }

        if (occurrence is not null)
        {
            AssistantDiaryOccurrence.EnsureConsistent(occurrence, requestedDate, requestedTime);
        }

        return new MealDraft
        {
            Description = NormalizeDescription(description),
            MealType = mealType,
            RequestedDate = requestedDate,
            RequestedTime = requestedTime,
            Occurrence = occurrence
        };
    }

    // Blank → no description. NUL is rejected (PostgreSQL text and jsonb cannot store it).
    private static string? NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var text = description.Trim();

        if (text.Length > MaxDescriptionLength)
        {
            throw new ArgumentException($"A description has at most {MaxDescriptionLength} characters.", MealDraftFields.Description);
        }

        if (text.Contains('\0'))
        {
            throw new ArgumentException("A description cannot contain NUL characters.", MealDraftFields.Description);
        }

        return text;
    }
}
