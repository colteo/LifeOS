using System.Net.Http.Json;
using LifeOS.Contracts.Onboarding;
using LifeOS.Contracts.Users;

namespace LifeOS.App.Services.Onboarding;

// First-run onboarding steps. Uses the authenticated pipeline; each step returns the user's state
// afterwards, in the same shape as GET /api/me.
public sealed class OnboardingApiClient
{
	private readonly HttpClient _httpClient;

	public OnboardingApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	public Task<ApiResult<MeResponse>> SetUpFinanceProfileAsync(string defaultCurrency, CancellationToken cancellationToken = default) =>
		PostAsync("api/onboarding/finance-profile", JsonContent.Create(new SetUpFinanceProfileRequest(defaultCurrency)), cancellationToken);

	public Task<ApiResult<MeResponse>> CompleteAsync(CancellationToken cancellationToken = default) =>
		PostAsync("api/onboarding/complete", content: null, cancellationToken);

	private async Task<ApiResult<MeResponse>> PostAsync(string path, HttpContent? content, CancellationToken cancellationToken)
	{
		try
		{
			using var response = await _httpClient.PostAsync(path, content, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<MeResponse>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var me = await response.Content.ReadFromJsonAsync<MeResponse>(cancellationToken);

			return me is null
				? ApiResult<MeResponse>.Failure("The LifeOS API returned an empty response.")
				: ApiResult<MeResponse>.Success(me);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<MeResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}
}
