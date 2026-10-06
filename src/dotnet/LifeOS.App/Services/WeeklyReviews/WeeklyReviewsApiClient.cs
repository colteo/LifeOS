using System.Net.Http.Json;
using LifeOS.Contracts.WeeklyReviews;

namespace LifeOS.App.Services.WeeklyReviews;

// AUTO-002: the saved weekly reviews (read-only) and the automatic weekly review setting. Failures
// carry the API's readable message (e.g. 404 "This weekly review does not exist.").
public sealed class WeeklyReviewsApiClient
{
	public const string ReviewsPath = "api/weekly-reviews";
	public const string SettingsPath = "api/weekly-reviews/settings";

	private readonly HttpClient _httpClient;

	public WeeklyReviewsApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	// Newest week first; pass the previous page's NextCursor for the next page.
	public Task<ApiResult<WeeklyReviewPageResponse>> GetPageAsync(string? cursor, CancellationToken cancellationToken = default) =>
		GetAsync<WeeklyReviewPageResponse>(cursor is null ? ReviewsPath : $"{ReviewsPath}?cursor={Uri.EscapeDataString(cursor)}", cancellationToken);

	public Task<ApiResult<WeeklyReviewResponse>> GetAsync(Guid reviewId, CancellationToken cancellationToken = default) =>
		GetAsync<WeeklyReviewResponse>($"{ReviewsPath}/{reviewId}", cancellationToken);

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
