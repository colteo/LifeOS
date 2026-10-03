using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Application.Nutrition;
using Microsoft.Extensions.Logging;

namespace LifeOS.Infrastructure.Nutrition;

// Where the Python AI service runs (ADR-011) and the secret it requires (PROD-AI-001). BaseUrl null:
// estimation is not configured, and every estimate is reported unavailable without any network call
// (the diary keeps working).
public sealed record NutritionAiOptions(Uri? BaseUrl, TimeSpan Timeout, string? ServiceKey = null)
{
    // Above the service's own worst case (bounded provider attempts and retry waits). Production sets a
    // longer NutritionAi:TimeoutSeconds to absorb a Render Free cold start of the AI service.
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static NutritionAiOptions Disabled { get; } = new(null, DefaultTimeout);

    // Never prints the service key (the compiler-generated record ToString would).
    public override string ToString() => $"NutritionAiOptions {{ BaseUrl = {BaseUrl}, Timeout = {Timeout} }}";
}

// Client of POST /v1/nutrition/estimate-meal. Sends ONLY the meal description and optional meal type;
// never user ids, meal ids, dates or user tokens. Authenticates with the service key as a Bearer token
// (PROD-AI-001). Every failure is a result, never an exception: the service's status codes and
// messages do not leave this class, and neither the meal text nor the key is ever logged.
internal sealed class NutritionEstimationClient : INutritionEstimationService, IDisposable
{
    private const string EstimatePath = "v1/nutrition/estimate-meal";

    private readonly HttpClient? _httpClient;
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly ILogger<NutritionEstimationClient> _logger;

    public NutritionEstimationClient(HttpClient? httpClient, string? serviceKey, ILogger<NutritionEstimationClient> logger)
    {
        if (httpClient is not null && string.IsNullOrWhiteSpace(serviceKey))
        {
            throw new ArgumentException("A configured AI service requires a service key.", nameof(serviceKey));
        }

        _httpClient = httpClient;
        _authorization = httpClient is null ? null : new AuthenticationHeaderValue("Bearer", serviceKey!.Trim());
        _logger = logger;
    }

    // Strict: numbers must be JSON numbers (no quoted numbers), names are the service's snake_case.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<NutritionEstimationResult> EstimateAsync(MealEstimationInput input, CancellationToken cancellationToken)
    {
        if (_httpClient is null)
        {
            return NutritionEstimationResult.Failed(NutritionEstimationFailure.Unavailable);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, EstimatePath)
            {
                Content = JsonContent.Create(new EstimateMealPayload(input.Description, input.MealType?.ToString()), options: JsonOptions)
            };
            request.Headers.Authorization = _authorization;

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogWarning("Nutrition AI service returned HTTP {StatusCode}.", (int)response.StatusCode);

                // 502: the provider answered without a usable estimate. 422: the service rejected the
                // input. Anything else (503, 500, 401 for a rejected service key, ...) means the service
                // cannot estimate right now.
                return NutritionEstimationResult.Failed(response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.UnprocessableEntity
                    ? NutritionEstimationFailure.NotEstimable
                    : NutritionEstimationFailure.Unavailable);
            }

            var estimate = await response.Content.ReadFromJsonAsync<EstimatePayload>(JsonOptions, cancellationToken);

            if (estimate is not { CaloriesKcal: { } calories, ProteinGrams: { } protein, CarbsGrams: { } carbs, FatGrams: { } fat })
            {
                _logger.LogWarning("Nutrition AI service returned an incomplete estimate.");
                return NutritionEstimationResult.Failed(NutritionEstimationFailure.NotEstimable);
            }

            return NutritionEstimationResult.Success(new NutritionEstimate(calories, protein, carbs, fat, estimate.Assumptions ?? []));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            _logger.LogWarning("Nutrition AI service returned an unreadable estimate.");
            return NutritionEstimationResult.Failed(NutritionEstimationFailure.NotEstimable);
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning("Nutrition AI service is unreachable or timed out ({ExceptionType}).", exception.GetType().Name);
            return NutritionEstimationResult.Failed(NutritionEstimationFailure.Unavailable);
        }
    }

    public void Dispose() => _httpClient?.Dispose();

    // The exact external payload: {"description": "...", "meal_type": "Lunch" | null}.
    private sealed record EstimateMealPayload(string Description, string? MealType);

    private sealed record EstimatePayload(decimal? CaloriesKcal, decimal? ProteinGrams, decimal? CarbsGrams, decimal? FatGrams,
        List<string>? Assumptions);
}
