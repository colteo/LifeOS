using System.Net;
using System.Net.Http.Headers;

namespace LifeOS.App.Services.Auth;

// Sends the LifeOS access token with every request of the API clients that use it (never with the
// auth endpoints, which use a plain HttpClient).
//
// On 401: one single-flight refresh, then ONE retry with a new cloned request (an HttpRequestMessage
// is never sent twice). At most: original request → refresh → one retry.
public sealed class AuthorizationMessageHandler : DelegatingHandler
{
	private readonly TokenSession _session;

	public AuthorizationMessageHandler(TokenSession session, HttpMessageHandler innerHandler)
		: base(innerHandler)
	{
		_session = session;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		// Buffer the body first, so the retry can carry an identical copy.
		var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);

		var sentAccessToken = _session.AccessToken;
		SetBearer(request, sentAccessToken);

		var response = await base.SendAsync(request, cancellationToken);

		if (response.StatusCode != HttpStatusCode.Unauthorized)
		{
			return response;
		}

		var update = await _session.RefreshAsync(sentAccessToken, cancellationToken);

		if (update != SessionUpdate.Established)
		{
			// Rejected: the session has ended. Unavailable: the caller sees the original 401.
			return response;
		}

		var retry = Clone(request, body);
		SetBearer(retry, _session.AccessToken);
		response.Dispose();

		var retryResponse = await base.SendAsync(retry, cancellationToken);

		if (retryResponse.StatusCode == HttpStatusCode.Unauthorized)
		{
			// A freshly refreshed token was refused: the session is no longer usable.
			await _session.EndAsync("Your session is no longer valid. Please sign in again.");
		}

		return retryResponse;
	}

	private static void SetBearer(HttpRequestMessage request, string? accessToken) =>
		request.Headers.Authorization = accessToken is null ? null : new AuthenticationHeaderValue("Bearer", accessToken);

	// A new message with the same method, URI, version, headers (except Authorization, set again),
	// body, content headers and options.
	private static HttpRequestMessage Clone(HttpRequestMessage request, byte[]? body)
	{
		var clone = new HttpRequestMessage(request.Method, request.RequestUri)
		{
			Version = request.Version,
			VersionPolicy = request.VersionPolicy
		};

		foreach (var header in request.Headers)
		{
			if (header.Key != "Authorization")
			{
				clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}
		}

		if (body is not null)
		{
			clone.Content = new ByteArrayContent(body);

			foreach (var header in request.Content!.Headers)
			{
				clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
			}
		}

		foreach (var option in request.Options)
		{
			((IDictionary<string, object?>)clone.Options)[option.Key] = option.Value;
		}

		return clone;
	}
}
