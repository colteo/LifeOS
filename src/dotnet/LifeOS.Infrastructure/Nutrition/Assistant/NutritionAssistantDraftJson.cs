using System.Globalization;
using System.Text.Json;
using LifeOS.Domain.Assistant;
using LifeOS.Domain.Nutrition;
using LifeOS.Domain.Nutrition.Assistant;
using LifeOS.Domain.Nutrition.Hydration.Assistant;
using LifeOS.Infrastructure.Assistant;

namespace LifeOS.Infrastructure.Nutrition.Assistant;

// AI-008: the jsonb documents of the diary drafts. Version 1:
//
//   hydration.intake.create: { "amountMl": 300|null, "requestedDate": "2026-10-27"|null, "requestedTime": "08:30"|null,
//                              "occurrence": {...}|null }
//   nutrition.meal.create:   { "description": string|null, "mealType": "Lunch"|null, "requestedDate", "requestedTime",
//                              "occurrence": {...}|null }
//   occurrence:              { "localDate", "localTime" ("HH:mm"), "dateSource", "timeSource", "timeZoneId", "occurredAtUtc" }
//
// Enums are their names, dates/times ISO. Reading goes through the Domain factories, so a stored draft is validated
// again; an unknown payload version is an error, never a guess.
internal static class DiaryDraftJson
{
    public const string DateFormat = "yyyy-MM-dd";
    public const string TimeFormat = "HH:mm";

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public static string? Date(DateOnly? date) => date?.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static string? Time(TimeOnly? time) => time?.ToString(TimeFormat, CultureInfo.InvariantCulture);

    public static DateOnly ParseDate(string value) => DateOnly.ParseExact(value, DateFormat, CultureInfo.InvariantCulture);

    public static TimeOnly ParseTime(string value) => TimeOnly.ParseExact(value, TimeFormat, CultureInfo.InvariantCulture);

    public static OccurrenceV1? ToDocument(AssistantDiaryOccurrence? occurrence) => occurrence is null
        ? null
        : new OccurrenceV1(Date(occurrence.LocalDate)!, Time(occurrence.LocalTime)!, occurrence.DateSource.ToString(),
            occurrence.TimeSource.ToString(), occurrence.TimeZoneId, occurrence.OccurredAtUtc);

    public static AssistantDiaryOccurrence? FromDocument(OccurrenceV1? document) => document is null
        ? null
        : AssistantDiaryOccurrence.Create(ParseDate(document.LocalDate), ParseTime(document.LocalTime),
            Enum.Parse<AssistantDraftDateSource>(document.DateSource), Enum.Parse<AssistantDraftTimeSource>(document.TimeSource),
            document.TimeZoneId, document.OccurredAtUtc);

    public sealed record OccurrenceV1(
        string LocalDate,
        string LocalTime,
        string DateSource,
        string TimeSource,
        string TimeZoneId,
        DateTimeOffset OccurredAtUtc);
}

internal sealed class HydrationIntakeDraftCodec : IAssistantDraftCodec<HydrationIntakeDraft>
{
    public static HydrationIntakeDraftCodec Instance { get; } = new();

    public string Kind => AssistantOperationKinds.HydrationIntakeCreate;

    public string Serialize(int payloadVersion, HydrationIntakeDraft draft) => payloadVersion switch
    {
        1 => JsonSerializer.Serialize(new DraftV1(draft.AmountMl, DiaryDraftJson.Date(draft.RequestedDate), DiaryDraftJson.Time(draft.RequestedTime),
            DiaryDraftJson.ToDocument(draft.Occurrence)), DiaryDraftJson.Options),
        _ => throw new NotSupportedException($"Hydration draft payload version {payloadVersion} cannot be written.")
    };

    public HydrationIntakeDraft Deserialize(int payloadVersion, string json)
    {
        if (payloadVersion != 1)
        {
            throw new NotSupportedException($"Hydration draft payload version {payloadVersion} is not supported.");
        }

        var document = JsonSerializer.Deserialize<DraftV1>(json, DiaryDraftJson.Options) ?? throw new InvalidOperationException("The hydration draft is empty.");

        return HydrationIntakeDraft.Create(document.AmountMl,
            document.RequestedDate is { } date ? DiaryDraftJson.ParseDate(date) : null,
            document.RequestedTime is { } time ? DiaryDraftJson.ParseTime(time) : null,
            DiaryDraftJson.FromDocument(document.Occurrence));
    }

    private sealed record DraftV1(int? AmountMl, string? RequestedDate, string? RequestedTime, DiaryDraftJson.OccurrenceV1? Occurrence);
}

internal sealed class MealDraftCodec : IAssistantDraftCodec<MealDraft>
{
    public static MealDraftCodec Instance { get; } = new();

    public string Kind => AssistantOperationKinds.NutritionMealCreate;

    public string Serialize(int payloadVersion, MealDraft draft) => payloadVersion switch
    {
        1 => JsonSerializer.Serialize(new DraftV1(draft.Description, draft.MealType?.ToString(), DiaryDraftJson.Date(draft.RequestedDate),
            DiaryDraftJson.Time(draft.RequestedTime), DiaryDraftJson.ToDocument(draft.Occurrence)), DiaryDraftJson.Options),
        _ => throw new NotSupportedException($"Meal draft payload version {payloadVersion} cannot be written.")
    };

    public MealDraft Deserialize(int payloadVersion, string json)
    {
        if (payloadVersion != 1)
        {
            throw new NotSupportedException($"Meal draft payload version {payloadVersion} is not supported.");
        }

        var document = JsonSerializer.Deserialize<DraftV1>(json, DiaryDraftJson.Options) ?? throw new InvalidOperationException("The meal draft is empty.");

        return MealDraft.Create(document.Description,
            document.MealType is { } type ? Enum.Parse<MealType>(type) : null,
            document.RequestedDate is { } date ? DiaryDraftJson.ParseDate(date) : null,
            document.RequestedTime is { } time ? DiaryDraftJson.ParseTime(time) : null,
            DiaryDraftJson.FromDocument(document.Occurrence));
    }

    private sealed record DraftV1(string? Description, string? MealType, string? RequestedDate, string? RequestedTime, DiaryDraftJson.OccurrenceV1? Occurrence);
}
