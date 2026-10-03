namespace LifeOS.Contracts.Nutrition;

// MealType: "Breakfast", "Lunch", "Dinner", "Snack", "Other", or null for none.
// Date is the diary day; Time is the local wall-clock time; UtcOffsetMinutes is the device's offset
// from UTC for that date and time (e.g. 120 for UTC+02:00).
public sealed record CreateMealRequest(string? Description, string? MealType, DateOnly? Date, TimeOnly? Time, int? UtcOffsetMinutes);

// The meal stays on its diary day.
public sealed record UpdateMealRequest(string? Description, string? MealType, TimeOnly? Time);

public sealed record MealResponse(
    Guid Id,
    string Description,
    string? MealType,
    DateOnly Date,
    TimeOnly Time,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
