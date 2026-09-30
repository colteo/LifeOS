using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.App.Services.Auth;
using LifeOS.Contracts.Users;

namespace LifeOS.App.Services.Users;

// GET /api/me through the authenticated pipeline (AuthorizationMessageHandler).
public sealed class MeApiClient
{
	private readonly HttpClient _httpClient;

	public MeApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	public async Task<AuthCallResult<MeResponse>> GetMeAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.GetAsync("api/me", cancellationToken);

			// 401 after the handler's refresh/retry, or 404 for a user that no longer exists.
			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
			{
				return AuthCallResult<MeResponse>.Rejected();
			}

			if (!response.IsSuccessStatusCode)
			{
				return AuthCallResult<MeResponse>.Unavailable();
			}

			var me = await response.Content.ReadFromJsonAsync<MeResponse>(cancellationToken);

			return me is null ? AuthCallResult<MeResponse>.Unavailable() : AuthCallResult<MeResponse>.Success(me);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken) || exception is JsonException)
		{
			return AuthCallResult<MeResponse>.Unavailable();
		}
	}
}
