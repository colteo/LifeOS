using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using LifeOS.Contracts.Nutrition;

namespace LifeOS.App.Services.Nutrition;

public sealed class NutritionApiClient
{
	private const string MealsPath = "api/nutrition/meals";

	// 404: deleted meanwhile (or never this user's).
	private const string NotFoundMessage = "This meal no longer exists.";

	private readonly HttpClient _httpClient;

	public NutritionApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
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

	private static async Task<ApiResult<MealResponse>> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
	{
		try
		{
			using var response = await send();

			if (response.StatusCode == HttpStatusCode.NotFound)
			{
				return ApiResult<MealResponse>.Failure(NotFoundMessage);
			}

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<MealResponse>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var meal = await response.Content.ReadFromJsonAsync<MealResponse>(cancellationToken);

			return meal is null
				? ApiResult<MealResponse>.Failure("The LifeOS API returned an empty response.")
				: ApiResult<MealResponse>.Success(meal);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<MealResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}
}
