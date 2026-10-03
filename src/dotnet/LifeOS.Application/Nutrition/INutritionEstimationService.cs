using LifeOS.Domain.Nutrition;

namespace LifeOS.Application.Nutrition;

// The AI capability NUT-002 needs: estimate ONE meal. Implemented in Infrastructure by a client of the
// Python AI service; no provider, model or framework is visible here.
//
// Privacy boundary: the input is exactly the meal's text and optional type. No user id, meal id,
// dates, times or any other LifeOS data is ever passed to the estimator.
public interface INutritionEstimationService
{
    // Never throws for expected failures (unreachable, timeout, unusable output): those are results.
    Task<NutritionEstimationResult> EstimateAsync(MealEstimationInput input, CancellationToken cancellationToken);
}

public sealed record MealEstimationInput(string Description, MealType? MealType);

// The estimator's raw proposal; Application validates it into NutritionValues before any use.
public sealed record NutritionEstimate(
    decimal CaloriesKcal,
    decimal ProteinGrams,
    decimal CarbsGrams,
    decimal FatGrams,
    IReadOnlyList<string> Assumptions);

public enum NutritionEstimationFailure
{
    // Not configured, unreachable, timed out or rate-limited: try again later.
    Unavailable,

    // The estimator answered without a usable estimate for this meal.
    NotEstimable
}

public sealed record NutritionEstimationResult(NutritionEstimate? Estimate, NutritionEstimationFailure? Failure)
{
    public static NutritionEstimationResult Success(NutritionEstimate estimate) => new(estimate, null);

    public static NutritionEstimationResult Failed(NutritionEstimationFailure failure) => new(null, failure);
}
