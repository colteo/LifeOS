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
}
