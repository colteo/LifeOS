using System.Net.Http.Json;
using LifeOS.Contracts.WeeklyReviews;

namespace LifeOS.App.Services.WeeklyReviews;

// AUTO-002: the saved weekly reviews (read-only) and the automatic weekly review setting.
// AI-001: the review's AI Insights (read; generate on demand). Failures carry the API's readable
// message (e.g. 404 "This weekly review does not exist.", 503 "AI insights are unavailable right now.").
public sealed class WeeklyReviewsApiClient
{
	public const string ReviewsPath = "api/weekly-reviews";
	public const string SettingsPath = "api/weekly-reviews/settings";

	private readonly HttpClient _httpClient;

	// Generating insights reaches the AI service: that one call uses this client, which has the longer
	// AI timeout (ApiTimeouts.NutritionAi); every other call keeps the default one.
	private readonly HttpClient _aiHttpClient;

	public WeeklyReviewsApiClient(HttpClient httpClient, HttpClient? aiHttpClient = null)
	{
		_httpClient = httpClient;
		_aiHttpClient = aiHttpClient ?? httpClient;
	}

	// Newest week first; pass the previous page's NextCursor for the next page.
	public Task<ApiResult<WeeklyReviewPageResponse>> GetPageAsync(string? cursor, CancellationToken cancellationToken = default) =>
		GetAsync<WeeklyReviewPageResponse>(cursor is null ? ReviewsPath : $"{ReviewsPath}?cursor={Uri.EscapeDataString(cursor)}", cancellationToken);

	public Task<ApiResult<WeeklyReviewResponse>> GetAsync(Guid reviewId, CancellationToken cancellationToken = default) =>
		GetAsync<WeeklyReviewResponse>($"{ReviewsPath}/{reviewId}", cancellationToken);

	// Never generates: the stored insights or "NotGenerated".
	public Task<ApiResult<WeeklyReviewInsightsStateResponse>> GetInsightsAsync(Guid reviewId, CancellationToken cancellationToken = default) =>
		GetAsync<WeeklyReviewInsightsStateResponse>(InsightsPath(reviewId), cancellationToken);

	// Generates once; afterwards returns the stored insights.
	public async Task<ApiResult<WeeklyReviewInsightsStateResponse>> GenerateInsightsAsync(Guid reviewId, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _aiHttpClient.PostAsync(InsightsPath(reviewId), null, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<WeeklyReviewInsightsStateResponse>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var value = await response.Content.ReadFromJsonAsync<WeeklyReviewInsightsStateResponse>(cancellationToken);

			return value is null
				? ApiResult<WeeklyReviewInsightsStateResponse>.Failure("The LifeOS API returned an empty response.")
				: ApiResult<WeeklyReviewInsightsStateResponse>.Success(value);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<WeeklyReviewInsightsStateResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	public Task<ApiResult<WeeklyReviewSettingsResponse>> GetSettingsAsync(CancellationToken cancellationToken = default) =>
		GetAsync<WeeklyReviewSettingsResponse>(SettingsPath, cancellationToken);

	public async Task<ApiResult<bool>> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.PutAsJsonAsync(SettingsPath, new SetWeeklyReviewSettingsRequest(enabled), cancellationToken);

			return response.IsSuccessStatusCode
				? ApiResult<bool>.Success(enabled)
				: ApiResult<bool>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<bool>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	private static string InsightsPath(Guid reviewId) => $"{ReviewsPath}/{reviewId}/insights";

	private async Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken)
		where T : class
	{
		try
		{
			using var response = await _httpClient.GetAsync(path, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<T>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);

			return value is null
				? ApiResult<T>.Failure("The LifeOS API returned an empty response.")
				: ApiResult<T>.Success(value);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<T>.Failure(ApiErrors.UnreachableMessage);
		}
	}
}
