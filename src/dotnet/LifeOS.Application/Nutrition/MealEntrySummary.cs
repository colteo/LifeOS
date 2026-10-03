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
    DateTimeOffset UpdatedAtUtc)
{
    public static MealEntrySummary From(MealEntry entry) => new(entry.Id, entry.Description, entry.MealType,
        entry.DiaryDate, entry.DiaryTime, entry.OccurredAtUtc, entry.CreatedAtUtc, entry.UpdatedAtUtc);
}

public enum MealResultStatus
{
    Ok,
    Invalid,
    NotFound
}

// The outcome of a meal use case. Invalid carries the request field and a readable message.
public sealed record MealResult(MealResultStatus Status, MealEntrySummary? Meal = null, string? Field = null, string? Message = null)
{
    public static MealResult Ok(MealEntry entry) => new(MealResultStatus.Ok, MealEntrySummary.From(entry));

    public static MealResult Invalid(string field, string message) => new(MealResultStatus.Invalid, Field: field, Message: message);

    public static MealResult NotFound() => new(MealResultStatus.NotFound);

    // Domain validation failures name the offending parameter; it is also the request field name.
    public static MealResult Invalid(ArgumentException exception) =>
        Invalid(exception.ParamName ?? "request", exception.Message);
}
