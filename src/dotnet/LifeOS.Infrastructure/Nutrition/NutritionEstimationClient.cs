using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Application.Nutrition;
using Microsoft.Extensions.Logging;

namespace LifeOS.Infrastructure.Nutrition;

// Where the Python AI service runs (ADR-011). BaseUrl null: estimation is not configured, and every
// estimate is reported unavailable without any network call (the diary keeps working).
public sealed record NutritionAiOptions(Uri? BaseUrl, TimeSpan Timeout)
{
    // Above the service's own worst case (bounded provider attempts and retry waits).
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static NutritionAiOptions Disabled { get; } = new(null, DefaultTimeout);
}

// Client of POST /v1/nutrition/estimate-meal. Sends ONLY the meal description and optional meal type;
// never user ids, meal ids, dates or tokens. Every failure is a result, never an exception: the
// service's status codes and messages do not leave this class, and the meal text is never logged.
internal sealed class NutritionEstimationClient(HttpClient? httpClient, ILogger<NutritionEstimationClient> logger)
    : INutritionEstimationService, IDisposable
{
    private const string EstimatePath = "v1/nutrition/estimate-meal";

    // Strict: numbers must be JSON numbers (no quoted numbers), names are the service's snake_case.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<NutritionEstimationResult> EstimateAsync(MealEstimationInput input, CancellationToken cancellationToken)
    {
        if (httpClient is null)
        {
            return NutritionEstimationResult.Failed(NutritionEstimationFailure.Unavailable);
        }

        try
        {
            using var response = await httpClient.PostAsJsonAsync(EstimatePath,
                new EstimateMealPayload(input.Description, input.MealType?.ToString()), JsonOptions, cancellationToken);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning("Nutrition AI service returned HTTP {StatusCode}.", (int)response.StatusCode);

                // 502: the provider answered without a usable estimate. 422: the service rejected the
                // input. Anything else (503, 500, ...) means the service cannot estimate right now.
                return NutritionEstimationResult.Failed(response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.UnprocessableEntity
                    ? NutritionEstimationFailure.NotEstimable
                    : NutritionEstimationFailure.Unavailable);
            }

            var estimate = await response.Content.ReadFromJsonAsync<EstimatePayload>(JsonOptions, cancellationToken);

            if (estimate is not { CaloriesKcal: { } calories, ProteinGrams: { } protein, CarbsGrams: { } carbs, FatGrams: { } fat })
            {
                logger.LogWarning("Nutrition AI service returned an incomplete estimate.");
                return NutritionEstimationResult.Failed(NutritionEstimationFailure.NotEstimable);
            }

            return NutritionEstimationResult.Success(new NutritionEstimate(calories, protein, carbs, fat, estimate.Assumptions ?? []));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            logger.LogWarning("Nutrition AI service returned an unreadable estimate.");
            return NutritionEstimationResult.Failed(NutritionEstimationFailure.NotEstimable);
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("Nutrition AI service is unreachable or timed out ({ExceptionType}).", exception.GetType().Name);
            return NutritionEstimationResult.Failed(NutritionEstimationFailure.Unavailable);
        }
    }

    public void Dispose() => httpClient?.Dispose();

    // The exact external payload: {"description": "...", "meal_type": "Lunch" | null}.
    private sealed record EstimateMealPayload(string Description, string? MealType);

    private sealed record EstimatePayload(decimal? CaloriesKcal, decimal? ProteinGrams, decimal? CarbsGrams, decimal? FatGrams,
        List<string>? Assumptions);
}
