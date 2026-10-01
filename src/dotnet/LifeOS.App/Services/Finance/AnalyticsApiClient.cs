using System.Net.Http.Json;
using LifeOS.Contracts.Finance.Analytics;

namespace LifeOS.App.Services.Finance;

public sealed class AnalyticsApiClient
{
	private const string MonthlyPath = "api/analytics/monthly";

	private readonly HttpClient _httpClient;

	public AnalyticsApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	// One local calendar month as a half-open UTC range (see LocalMonth); the totals are computed
	// server-side, one block per currency.
	public async Task<ApiResult<MonthlyAnalyticsResponse>> GetMonthlyAsync(
		DateTimeOffset fromUtc,
		DateTimeOffset toUtc,
		CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.GetAsync(
				$"{MonthlyPath}?fromUtc={UtcQueryValue.Format(fromUtc)}&toUtc={UtcQueryValue.Format(toUtc)}",
				cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<MonthlyAnalyticsResponse>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var analytics = await response.Content.ReadFromJsonAsync<MonthlyAnalyticsResponse>(cancellationToken);

			return analytics is null
				? ApiResult<MonthlyAnalyticsResponse>.Failure("The LifeOS API returned an empty response.")
				: ApiResult<MonthlyAnalyticsResponse>.Success(analytics);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<MonthlyAnalyticsResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}
}
