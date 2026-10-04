namespace LifeOS.Contracts.Nutrition;

// NUT-003: manual daily targets. Amounts are kcal and grams with at most one decimal place.

// The target that applies on Date (the requested day, or the user's local today for "current").
// Target is null when there is none: nothing set yet, or targets were removed.
public sealed record NutritionTargetStateResponse(
    DateOnly Date,
    NutritionTargetResponse? Target);

// A null metric is not targeted. Source: "Manual".
public sealed record NutritionTargetResponse(
    DateOnly EffectiveFrom,
    decimal? CaloriesKcal,
    decimal? ProteinGrams,
    decimal? CarbsGrams,
    decimal? FatGrams,
    string Source);

// Applies from the user's local today, derived from the server clock and UtcOffsetMinutes (the
// device's current offset from UTC). There is no effective date: targets are never set for another day.
// Each metric is optional, but at least one is required; remove targets to have none.
public sealed record SetNutritionTargetRequest(
    decimal? CaloriesKcal,
    decimal? ProteinGrams,
    decimal? CarbsGrams,
    decimal? FatGrams,
    int? UtcOffsetMinutes);
