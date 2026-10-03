namespace LifeOS.Contracts.Nutrition;

// MealType: "Breakfast", "Lunch", "Dinner", "Snack", "Other", or null for none.
// Date is the diary day; Time is the local wall-clock time; UtcOffsetMinutes is the device's offset
// from UTC for that date and time (e.g. 120 for UTC+02:00).
public sealed record CreateMealRequest(string? Description, string? MealType, DateOnly? Date, TimeOnly? Time, int? UtcOffsetMinutes);

// The meal stays on its diary day. ClearNutrition confirms that changing the description of a meal
// with nutrition removes that nutrition (NUT-002); without it such a change is 409.
public sealed record UpdateMealRequest(string? Description, string? MealType, TimeOnly? Time, bool? ClearNutrition = null);

public sealed record MealResponse(
    Guid Id,
    string Description,
    string? MealType,
    DateOnly Date,
    TimeOnly Time,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    MealNutritionResponse? Nutrition = null);
