namespace LifeOS.Contracts.Nutrition;

// NUT-003: nutrition target planning. Amounts are kcal and grams with at most one decimal place.

// One target. A null metric is not targeted; at least one is present when a target exists.
public sealed record NutritionTargetValuesDto(
    decimal? CaloriesKcal,
    decimal? ProteinGrams,
    decimal? CarbsGrams,
    decimal? FatGrams);

// Weekday: "Monday" ... "Sunday". Mode: "Default" (the plan's default target), "Custom" (Target is
// required) or "NoTarget".
public sealed record NutritionTargetDayRuleDto(
    string? Weekday,
    string? Mode,
    NutritionTargetValuesDto? Target);

// A bounded period, both dates inclusive and required. DefaultTarget is optional; WeeklyRules has
// exactly one rule per weekday. Plans of one user never overlap (409 otherwise).
public sealed record NutritionTargetPlanRequest(
    DateOnly? StartsOn,
    DateOnly? EndsOn,
    NutritionTargetValuesDto? DefaultTarget,
    IReadOnlyList<NutritionTargetDayRuleDto>? WeeklyRules);

// WeeklyRules: Monday to Sunday.
public sealed record NutritionTargetPlanResponse(
    Guid Id,
    DateOnly StartsOn,
    DateOnly EndsOn,
    NutritionTargetValuesDto? DefaultTarget,
    IReadOnlyList<NutritionTargetDayRuleDto> WeeklyRules);

// Mode: "Custom" (Target is required) or "NoTarget".
public sealed record NutritionTargetOverrideDto(
    string? Mode,
    NutritionTargetValuesDto? Target);

// The target for one date, resolved from its plan, weekday rule and override. CoveredByPlan: whether a
// plan covers the date (only then can the day be overridden). Override: the date's override, if any.
public sealed record ResolvedNutritionTargetResponse(
    DateOnly Date,
    NutritionTargetValuesDto? Target,
    bool CoveredByPlan,
    NutritionTargetOverrideDto? Override);
