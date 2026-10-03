using LifeOS.Domain.Nutrition;

namespace LifeOS.Application.Nutrition;

public sealed record MealEntrySummary(
    Guid Id,
    string Description,
    MealType? MealType,
    DateOnly DiaryDate,
    TimeOnly DiaryTime,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    MealNutritionSummary? Nutrition = null)
{
    public static MealEntrySummary From(MealEntry entry, MealNutritionSnapshot? nutrition = null) => new(entry.Id,
        entry.Description, entry.MealType, entry.DiaryDate, entry.DiaryTime, entry.OccurredAtUtc, entry.CreatedAtUtc,
        entry.UpdatedAtUtc, nutrition is null ? null : MealNutritionSummary.From(nutrition));

    public static MealEntrySummary From(MealWithNutrition meal) => From(meal.Meal, meal.Nutrition);
}

// A meal's current nutrition (NUT-002).
public sealed record MealNutritionSummary(NutritionValues Values, NutritionSource Source, DateTimeOffset UpdatedAtUtc)
{
    public static MealNutritionSummary From(MealNutritionSnapshot snapshot) =>
        new(snapshot.Values, snapshot.Source, snapshot.UpdatedAtUtc);
}

public enum MealResultStatus
{
    Ok,
    Invalid,
    NotFound,

    // The description change would discard the meal's nutrition; the caller must confirm it.
    NutritionClearRequired
}

// The outcome of a meal use case. Invalid carries the request field and a readable message.
public sealed record MealResult(MealResultStatus Status, MealEntrySummary? Meal = null, string? Field = null, string? Message = null)
{
    public const string NutritionClearMessage = "Changing the meal description will clear its nutrition analysis.";

    public static MealResult Ok(MealEntry entry, MealNutritionSnapshot? nutrition = null) =>
        new(MealResultStatus.Ok, MealEntrySummary.From(entry, nutrition));

    public static MealResult Invalid(string field, string message) => new(MealResultStatus.Invalid, Field: field, Message: message);

    public static MealResult NotFound() => new(MealResultStatus.NotFound);

    public static MealResult NutritionClearRequired() =>
        new(MealResultStatus.NutritionClearRequired, Field: "clearNutrition", Message: NutritionClearMessage);

    // Domain validation failures name the offending parameter; it is also the request field name.
    public static MealResult Invalid(ArgumentException exception) =>
        Invalid(exception.ParamName ?? "request", exception.Message);
}
