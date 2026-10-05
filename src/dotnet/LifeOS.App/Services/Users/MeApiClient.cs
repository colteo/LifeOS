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
	private readonly TokenSession? _session;

	public MeApiClient(HttpClient httpClient, TokenSession? session = null)
	{
		_httpClient = httpClient;
		_session = session;
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

	// PUT /api/me/time-zone (AUTO-001). True only when the server acknowledged the zone (204).
	// Every other outcome (400, 401, 404, server error, network failure) is just "not acknowledged":
	// time zone sync is best effort and never decides anything about the session.
	public async Task<bool> SetTimeZoneAsync(string timeZoneId, CancellationToken cancellationToken = default)
	{
		try
		{
			var sessionVersion = _session?.Version;
			using var request = new HttpRequestMessage(HttpMethod.Put, "api/me/time-zone")
			{
				Content = JsonContent.Create(new SetTimeZoneRequest(timeZoneId))
			};
			if (sessionVersion is { } version)
			{
				request.Options.Set(AuthorizationMessageHandler.BindToSession, version);
			}
			using var response = await _httpClient.SendAsync(request, cancellationToken);

			return response.StatusCode == HttpStatusCode.NoContent;
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return false;
		}
	}
}
