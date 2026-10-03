using System.Collections.Concurrent;
using LifeOS.Application.Nutrition;

namespace LifeOS.UnitTests.Fakes;

// The AI port without any AI: answers from a rule (by description) and records every input, so tests can
// assert exactly what would have been sent and how many estimates were requested.
internal sealed class FakeNutritionEstimationService : INutritionEstimationService
{
    private readonly ConcurrentQueue<MealEstimationInput> _inputs = new();

    // Default: a fixed, valid estimate for any meal.
    public Func<MealEstimationInput, NutritionEstimationResult> Respond { get; set; } = _ => Estimate(620, 52, 58, 20);

    // Called before answering; lets a test interleave other work with an in-flight estimate.
    public Func<MealEstimationInput, Task>? BeforeAnswer { get; set; }

    public IReadOnlyList<MealEstimationInput> Inputs => _inputs.ToList();

    public static NutritionEstimationResult Estimate(decimal calories, decimal protein, decimal carbs, decimal fat, params string[] assumptions) =>
        NutritionEstimationResult.Success(new NutritionEstimate(calories, protein, carbs, fat, assumptions));

    public static NutritionEstimationResult Unavailable => NutritionEstimationResult.Failed(NutritionEstimationFailure.Unavailable);

    public static NutritionEstimationResult NotEstimable => NutritionEstimationResult.Failed(NutritionEstimationFailure.NotEstimable);

    public async Task<NutritionEstimationResult> EstimateAsync(MealEstimationInput input, CancellationToken cancellationToken)
    {
        _inputs.Enqueue(input);

        if (BeforeAnswer is { } before)
        {
            await before(input);
        }

        return Respond(input);
    }
}
