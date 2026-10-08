using System.Net.Http.Json;
using LifeOS.Contracts.ActionAgent;

namespace LifeOS.App.Services.ActionAgent;

// AI-002: a weekly review's suggested action (read; analyse on demand) and the user's decision on it.
// Failures carry the API's readable message (e.g. 503 "The assistant is unavailable right now.").
public sealed class SuggestedActionApiClient
{
	public const string ProposalsPath = "api/action-proposals";

	private readonly HttpClient _httpClient;

	// Analysing reaches the AI service (several model steps): that one call uses this client, which has
	// the longer AI timeout (ApiTimeouts.NutritionAi); every other call keeps the default one.
	private readonly HttpClient _aiHttpClient;

	public SuggestedActionApiClient(HttpClient httpClient, HttpClient? aiHttpClient = null)
	{
		_httpClient = httpClient;
		_aiHttpClient = aiHttpClient ?? httpClient;
	}

	// Never analyses: the review's latest proposal, or "None".
	public Task<ApiResult<SuggestedActionStateResponse>> GetForReviewAsync(Guid reviewId, CancellationToken cancellationToken = default) =>
		SendAsync<SuggestedActionStateResponse>(_httpClient, HttpMethod.Get, ReviewPath(reviewId), cancellationToken);

	// Runs the assistant; returns a proposal (to approve or reject) or "NoAction". Changes nothing.
	public Task<ApiResult<SuggestedActionStateResponse>> AnalyzeAsync(Guid reviewId, CancellationToken cancellationToken = default) =>
		SendAsync<SuggestedActionStateResponse>(_aiHttpClient, HttpMethod.Post, ReviewPath(reviewId), cancellationToken);

	// The explicit approval: the only call that changes the budget.
	public Task<ApiResult<ProposedActionResponse>> ApproveAsync(Guid proposalId, CancellationToken cancellationToken = default) =>
		SendAsync<ProposedActionResponse>(_httpClient, HttpMethod.Post, $"{ProposalsPath}/{proposalId}/approve", cancellationToken);

	public Task<ApiResult<ProposedActionResponse>> RejectAsync(Guid proposalId, CancellationToken cancellationToken = default) =>
		SendAsync<ProposedActionResponse>(_httpClient, HttpMethod.Post, $"{ProposalsPath}/{proposalId}/reject", cancellationToken);

	private static string ReviewPath(Guid reviewId) => $"api/weekly-reviews/{reviewId}/suggested-action";

	private static async Task<ApiResult<T>> SendAsync<T>(HttpClient client, HttpMethod method, string path, CancellationToken cancellationToken)
		where T : class
	{
		try
		{
			using var request = new HttpRequestMessage(method, path);
			using var response = await client.SendAsync(request, cancellationToken);

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
