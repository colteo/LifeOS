using System.Net;
using System.Net.Http.Json;
using LifeOS.Contracts.Finance.Categories;

namespace LifeOS.App.Services.Finance;

public sealed class CategoriesApiClient
{
	private const string CategoriesPath = "api/categories";

	private readonly HttpClient _httpClient;

	public CategoriesApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	public async Task<ApiResult<IReadOnlyList<CategoryResponse>>> GetCategoriesAsync(
		CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.GetAsync(CategoriesPath, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<IReadOnlyList<CategoryResponse>>.Failure(
					await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var categories = await response.Content.ReadFromJsonAsync<List<CategoryResponse>>(cancellationToken);

			return ApiResult<IReadOnlyList<CategoryResponse>>.Success(categories ?? []);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<IReadOnlyList<CategoryResponse>>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	public async Task<ApiResult<CategoryResponse>> CreateCategoryAsync(
		CreateCategoryRequest request,
		CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.PostAsJsonAsync(CategoriesPath, request, cancellationToken);

			// The two expected refusals get their own readable messages.
			switch (response.StatusCode)
			{
				case HttpStatusCode.Conflict:
					return ApiResult<CategoryResponse>.Failure("A category with this name already exists at this level.");

				case HttpStatusCode.NotFound:
					// The parent no longer exists (or is not the user's): the page's tree is stale.
					return ApiResult<CategoryResponse>.Failure("The parent category no longer exists. Reload the page.");
			}

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<CategoryResponse>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var category = await response.Content.ReadFromJsonAsync<CategoryResponse>(cancellationToken);

			return category is null
				? ApiResult<CategoryResponse>.Failure("The LifeOS API returned an empty response.")
				: ApiResult<CategoryResponse>.Success(category);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<CategoryResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}
}
