using System.Net;
using System.Net.Http.Json;
using LifeOS.App.Services.Auth;
using LifeOS.Contracts.Devices;

namespace LifeOS.App.Services.Notifications;

// PUT / DELETE /api/devices/{installationId} (AUTO-001 §12) through the authenticated pipeline.
// Each request is bound to the session that existed when it was created: after an account switch it
// is never sent with the next account's token. Best effort: every failure is just "false".
public sealed class DevicesApiClient
{
	private readonly HttpClient _httpClient;
	private readonly TokenSession? _session;

	public DevicesApiClient(HttpClient httpClient, TokenSession? session = null)
	{
		_httpClient = httpClient;
		_session = session;
	}

	public async Task<bool> RegisterAsync(string installationId, RegisterDeviceRequest registration, CancellationToken cancellationToken = default)
	{
		using var request = Bound(new HttpRequestMessage(HttpMethod.Put, Path(installationId))
		{
			Content = JsonContent.Create(registration)
		});

		return await SendAsync(request, cancellationToken) is HttpStatusCode.NoContent;
	}

	// 404: the server has no registration of this installation for the caller, which is the goal.
	public async Task<bool> UnregisterAsync(string installationId, CancellationToken cancellationToken = default)
	{
		using var request = Bound(new HttpRequestMessage(HttpMethod.Delete, Path(installationId)));

		return await SendAsync(request, cancellationToken) is HttpStatusCode.NoContent or HttpStatusCode.NotFound;
	}

	private static string Path(string installationId) => "api/devices/" + Uri.EscapeDataString(installationId);

	private HttpRequestMessage Bound(HttpRequestMessage request)
	{
		if (_session?.Version is { } version)
		{
			request.Options.Set(AuthorizationMessageHandler.BindToSession, version);
		}

		return request;
	}

	private async Task<HttpStatusCode?> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		try
		{
			using var response = await _httpClient.SendAsync(request, cancellationToken);

			return response.StatusCode;
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken) || exception is OperationCanceledException)
		{
			return null;
		}
	}
}
