using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using LifeOS.Contracts.Nutrition;

namespace LifeOS.App.Services.Nutrition;

public sealed class NutritionApiClient
{
	private const string MealsPath = "api/nutrition/meals";

	private const string NutritionPath = "api/nutrition";

	// 404: deleted meanwhile (or never this user's).
	private const string NotFoundMessage = "This meal no longer exists.";

	// 409 on a meal update: the description change would clear the meal's nutrition (NUT-002).
	public const string NutritionClearRequiredMessage = "Changing the meal description will clear its nutrition analysis.";

	private readonly HttpClient _httpClient;

	// PROD-AI-001: the calls that reach the AI service (Estimate, Analyze day, lazy close) use this client,
	// which has a longer timeout (ApiTimeouts.NutritionAi); every other call keeps the default one.
	private readonly HttpClient _aiHttpClient;

	public NutritionApiClient(HttpClient httpClient, HttpClient? aiHttpClient = null)
	{
		_httpClient = httpClient;
		_aiHttpClient = aiHttpClient ?? httpClient;
	}

	// One diary day, newest first (the API's order).
	public async Task<ApiResult<IReadOnlyList<MealResponse>>> GetMealsAsync(DateOnly date, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.GetAsync(
				$"{MealsPath}?date={date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<IReadOnlyList<MealResponse>>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var meals = await response.Content.ReadFromJsonAsync<List<MealResponse>>(cancellationToken);

			return ApiResult<IReadOnlyList<MealResponse>>.Success(meals ?? []);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<IReadOnlyList<MealResponse>>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	public Task<ApiResult<MealResponse>> CreateMealAsync(CreateMealRequest request, CancellationToken cancellationToken = default) =>
		SendAsync(() => _httpClient.PostAsJsonAsync(MealsPath, request, cancellationToken), cancellationToken);

	public Task<ApiResult<MealResponse>> UpdateMealAsync(Guid id, UpdateMealRequest request, CancellationToken cancellationToken = default) =>
		SendAsync(() => _httpClient.PutAsJsonAsync($"{MealsPath}/{id}", request, cancellationToken), cancellationToken);

	// ---- NUT-002 ----

	// Deterministic totals of one diary day.
	public Task<ApiResult<DailyNutritionSummaryResponse>> GetSummaryAsync(DateOnly date, CancellationToken cancellationToken = default) =>
		SendAsync<DailyNutritionSummaryResponse>(() => _httpClient.GetAsync($"{NutritionPath}/summary?date={DateText(date)}", cancellationToken),
			cancellationToken);

	// An AI proposal for one meal; nothing is stored until it is confirmed or edited.
	public Task<ApiResult<NutritionEstimateResponse>> EstimateAsync(Guid mealId, CancellationToken cancellationToken = default) =>
		SendAsync<NutritionEstimateResponse>(() => _aiHttpClient.PostAsync($"{MealsPath}/{mealId}/estimate", null, cancellationToken),
			cancellationToken);

	public Task<ApiResult<MealResponse>> SetNutritionAsync(Guid mealId, SetMealNutritionRequest request, CancellationToken cancellationToken = default) =>
		SendAsync(() => _httpClient.PutAsJsonAsync($"{MealsPath}/{mealId}/nutrition", request, cancellationToken), cancellationToken);

	public Task<ApiResult<NutritionAnalysisResponse>> AnalyzeDayAsync(DateOnly date, CancellationToken cancellationToken = default) =>
		SendAsync<NutritionAnalysisResponse>(() => _aiHttpClient.PostAsync($"{NutritionPath}/analyze?date={DateText(date)}", null, cancellationToken),
			cancellationToken);

	// Closes past days lazily; today (from the device's current UTC offset) is never analyzed.
	public Task<ApiResult<NutritionAnalysisResponse>> LazyCloseAsync(int utcOffsetMinutes, CancellationToken cancellationToken = default) =>
		SendAsync<NutritionAnalysisResponse>(() => _aiHttpClient.PostAsJsonAsync($"{NutritionPath}/lazy-close",
			new LazyCloseRequest(utcOffsetMinutes), cancellationToken), cancellationToken);

	public async Task<ApiResult<bool>> DeleteMealAsync(Guid id, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.DeleteAsync($"{MealsPath}/{id}", cancellationToken);

			if (response.StatusCode == HttpStatusCode.NotFound)
			{
				return ApiResult<bool>.Failure(NotFoundMessage);
			}

			return response.IsSuccessStatusCode
				? ApiResult<bool>.Success(true)
				: ApiResult<bool>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<bool>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	private static Task<ApiResult<MealResponse>> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken) =>
		SendAsync<MealResponse>(send, cancellationToken);

	private static async Task<ApiResult<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
		where T : class
	{
		try
		{
			using var response = await send();

			if (response.StatusCode == HttpStatusCode.NotFound)
			{
				return ApiResult<T>.Failure(NotFoundMessage);
			}

			if (response.StatusCode == HttpStatusCode.Conflict)
			{
				return ApiResult<T>.Failure(NutritionClearRequiredMessage);
			}

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

	private static string DateText(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
