namespace LifeOS.Contracts.Nutrition;

// NUT-002. Amounts are kcal and grams with at most one decimal place.
// Source: "AiConfirmed", "AiRequested", "AiAutoClosed" or "UserAdjusted".
public sealed record MealNutritionResponse(
    decimal CaloriesKcal,
    decimal ProteinGrams,
    decimal CarbsGrams,
    decimal FatGrams,
    string Source,
    DateTimeOffset UpdatedAtUtc);

// An AI proposal for one meal. Not persisted: confirm or edit it with SetMealNutritionRequest.
public sealed record NutritionEstimateResponse(
    decimal CaloriesKcal,
    decimal ProteinGrams,
    decimal CarbsGrams,
    decimal FatGrams,
    IReadOnlyList<string> Assumptions);

// Source: "AiConfirmed" (the proposal accepted unchanged) or "UserAdjusted" (edited values).
public sealed record SetMealNutritionRequest(
    decimal? CaloriesKcal,
    decimal? ProteinGrams,
    decimal? CarbsGrams,
    decimal? FatGrams,
    string? Source);

// Totals are sums over the AnalyzedMealCount meals that have nutrition; AllAnalyzed only when every
// one of MealCount (> 0) meals has it. Target (NUT-003): the daily target effective on Date, or null.
public sealed record DailyNutritionSummaryResponse(
    DateOnly Date,
    int MealCount,
    int AnalyzedMealCount,
    bool AllAnalyzed,
    decimal CaloriesKcal,
    decimal ProteinGrams,
    decimal CarbsGrams,
    decimal FatGrams,
    DailyNutritionTargetResponse? Target = null);

// NUT-003. A null metric is not targeted; at least one is present.
public sealed record DailyNutritionTargetResponse(
    decimal? CaloriesKcal,
    decimal? ProteinGrams,
    decimal? CarbsGrams,
    decimal? FatGrams);

// The outcome of Analyze day or lazy close. Summary: the analyzed day (Analyze day only).
public sealed record NutritionAnalysisResponse(
    int Analyzed,
    int Failed,
    bool EstimationUnavailable,
    bool MorePending,
    DailyNutritionSummaryResponse? Summary);

// The device's current offset from UTC in minutes: it decides which diary day is "today".
public sealed record LazyCloseRequest(int? UtcOffsetMinutes);
