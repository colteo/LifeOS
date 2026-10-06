using System.Net;
using System.Net.Http.Json;
using LifeOS.App.Services.Auth;
using LifeOS.Contracts.Notifications;

namespace LifeOS.App.Services.Notifications;

public enum TestNotificationStatus
{
	Sent,
	NoActiveDevice,
	PushDisabled,
	RateLimited,
	Failed
}

// Counts only on success; nothing from the response body is kept otherwise.
public sealed record TestNotificationResult(TestNotificationStatus Status, int Devices = 0, int Sent = 0, int Failed = 0);

// POST /api/notifications/test (AUTO-001 §12) through the authenticated pipeline, for Diagnostics.
// No body and no query string. Bound to the session that existed when the request was created, as
// DevicesApiClient: after an account switch it is never sent with the next account's token.
public sealed class NotificationsApiClient
{
	public const string TestPath = "api/notifications/test";

	private readonly HttpClient _httpClient;
	private readonly TokenSession? _session;

	public NotificationsApiClient(HttpClient httpClient, TokenSession? session = null)
	{
		_httpClient = httpClient;
		_session = session;
	}

	public async Task<TestNotificationResult> SendTestAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Post, TestPath);
			if (_session?.Version is { } version)
			{
				request.Options.Set(AuthorizationMessageHandler.BindToSession, version);
			}
			using var response = await _httpClient.SendAsync(request, cancellationToken);

			switch (response.StatusCode)
			{
				case HttpStatusCode.OK:
					var counts = await response.Content.ReadFromJsonAsync<TestNotificationResponse>(cancellationToken);
					return counts is null
						? new(TestNotificationStatus.Failed)
						: new(TestNotificationStatus.Sent, counts.Devices, counts.Sent, counts.Failed);
				case HttpStatusCode.Conflict:
					return new(TestNotificationStatus.NoActiveDevice);
				case HttpStatusCode.ServiceUnavailable:
					return new(TestNotificationStatus.PushDisabled);
				case HttpStatusCode.TooManyRequests:
					return new(TestNotificationStatus.RateLimited);
				default:
					return new(TestNotificationStatus.Failed);
			}
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return new(TestNotificationStatus.Failed);
		}
	}
}
