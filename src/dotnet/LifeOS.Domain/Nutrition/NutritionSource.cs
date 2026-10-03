namespace LifeOS.Domain.Nutrition;

// Where a meal's current nutrition came from (NUT-002). Every source counts for daily totals
// immediately; the source is provenance, not a confidence level, and never names a provider or model.
public enum NutritionSource
{
    // An explicit single-meal AI estimate that the user accepted unchanged.
    AiConfirmed,

    // Estimated because the user asked to analyze a day; no per-meal confirmation.
    AiRequested,

    // Estimated by the lazy close of a past day.
    AiAutoClosed,

    // Values entered or edited by the user.
    UserAdjusted
}
