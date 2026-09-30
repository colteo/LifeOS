using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Contracts.Auth;

namespace LifeOS.App.Services.Auth;

// Calls the LifeOS session endpoints (/api/auth/token, /refresh, /logout).
// Uses a plain HttpClient WITHOUT AuthorizationMessageHandler, so a refresh can never trigger
// another refresh. Tokens are never logged.
public sealed class AuthApiClient
{
	private readonly HttpClient _httpClient;

	public AuthApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	public Task<AuthCallResult<TokenResponse>> ExchangeCodeAsync(
		string code,
		string codeVerifier,
		CancellationToken cancellationToken = default) =>
		PostForTokensAsync("api/auth/token", new TokenExchangeRequest(code, codeVerifier), cancellationToken);

	public Task<AuthCallResult<TokenResponse>> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
		PostForTokensAsync("api/auth/refresh", new RefreshTokenRequest(refreshToken), cancellationToken);

	// Best effort: the local sign-out never depends on this call.
	public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.PostAsJsonAsync("api/auth/logout", new LogoutRequest(refreshToken), cancellationToken);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			// Ignored: the server session expires on its own.
		}
	}

	private async Task<AuthCallResult<TokenResponse>> PostForTokensAsync<TRequest>(
		string path,
		TRequest request,
		CancellationToken cancellationToken)
	{
		try
		{
			using var response = await _httpClient.PostAsJsonAsync(path, request, cancellationToken);

			// 400/401: the server refused this code or refresh token.
			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
			{
				return AuthCallResult<TokenResponse>.Rejected();
			}

			if (!response.IsSuccessStatusCode)
			{
				return AuthCallResult<TokenResponse>.Unavailable();
			}

			var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);

			return tokens is null
				? AuthCallResult<TokenResponse>.Unavailable()
				: AuthCallResult<TokenResponse>.Success(tokens);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken) || exception is JsonException)
		{
			return AuthCallResult<TokenResponse>.Unavailable();
		}
	}
}
