using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using LifeOS.Application.Notifications;
using LifeOS.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace LifeOS.Infrastructure.Notifications.Fcm;

// AUTO-001 §11: one message to one device through FCM HTTP v1
// (POST https://fcm.googleapis.com/v1/projects/<project-id>/messages:send), authorized with the
// service account's OAuth token from Google.Apis.Auth. Exactly one HTTP attempt per call: retries
// belong to the delivery rows (Phase A), never to a second layer here.
//
// Provider responses are reduced to PushSendResult. Logs carry only the HTTP status and FCM's
// stable error enum: never the token, the request or the response body.
internal sealed class FcmPushNotificationSender(
    HttpClient httpClient,
    ITokenAccess credential,
    string projectId,
    ILogger<FcmPushNotificationSender> logger) : IPushNotificationSender
{
    public static readonly Uri BaseAddress = new("https://fcm.googleapis.com/");

    // Bounded: one send must fit well inside the tick's 20 s budget.
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The single Android channel the app creates (AUTO-001 §11).
    public const string AndroidChannelId = "lifeos_general";

    private static readonly JsonSerializerOptions Json = new();

    // FCM v1 FcmError.errorCode values (https://firebase.google.com/docs/reference/fcm/rest/v1/ErrorCode).
    private static readonly HashSet<string> KnownErrorCodes =
        ["UNSPECIFIED_ERROR", "INVALID_ARGUMENT", "UNREGISTERED", "SENDER_ID_MISMATCH", "QUOTA_EXCEEDED", "UNAVAILABLE", "INTERNAL", "THIRD_PARTY_AUTH_ERROR"];

    public async Task<PushSendResult> SendAsync(PushTarget target, PushMessage message, CancellationToken cancellationToken)
    {
        if (target.Provider != PushProvider.Fcm)
        {
            return PushSendResult.Rejected;
        }

        string accessToken;

        try
        {
            accessToken = await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A server credential problem is never a device-token problem.
            logger.LogWarning("FCM access token unavailable ({ExceptionType}).", exception.GetType().Name);
            return PushSendResult.Transient;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/projects/{projectId}/messages:send")
        {
            Content = JsonContent.Create(Body(target, message), options: Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return PushSendResult.Accepted;
            }

            var (errorCode, tokenFieldViolation) = await ReadErrorAsync(response, cancellationToken);
            var result = Classify(response.StatusCode, errorCode, tokenFieldViolation);

            logger.LogWarning(
                "FCM send not accepted: HTTP {StatusCode}, FCM error {ErrorCode}, outcome {Outcome}.",
                (int)response.StatusCode,
                errorCode is not null && KnownErrorCodes.Contains(errorCode) ? errorCode : "none",
                result);

            return result;
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Network failure or HttpClient timeout: retried by the delivery row.
            logger.LogWarning("FCM send failed in transport ({ExceptionType}).", exception.GetType().Name);
            return PushSendResult.Transient;
        }
    }

    // UNREGISTERED / SENDER_ID_MISMATCH, or an INVALID_ARGUMENT on message.token: the token is dead.
    // 429 / QUOTA_EXCEEDED, 5xx, UNAVAILABLE / INTERNAL and 401 (our OAuth token): retry later.
    // Anything else (other 4xx, e.g. 403 permission or 404 for a wrong project id): rejected, without
    // touching the device registration.
    public static PushSendResult Classify(HttpStatusCode status, string? errorCode, bool tokenFieldViolation) => (status, errorCode) switch
    {
        (_, "UNREGISTERED" or "SENDER_ID_MISMATCH") => PushSendResult.TokenInvalid,
        (HttpStatusCode.BadRequest, _) when tokenFieldViolation => PushSendResult.TokenInvalid,
        (HttpStatusCode.TooManyRequests, _) or (_, "QUOTA_EXCEEDED") => PushSendResult.Transient,
        (_, "UNAVAILABLE" or "INTERNAL") => PushSendResult.Transient,
        (HttpStatusCode.Unauthorized, _) => PushSendResult.Transient,
        _ when (int)status >= 500 => PushSendResult.Transient,
        _ => PushSendResult.Rejected
    };

    // AUTO-001 §11 message: fixed copy, opaque data, Android tag = notification key, one channel.
    public static object Body(PushTarget target, PushMessage message) => new
    {
        message = new
        {
            token = target.Token,
            notification = new { title = message.Title, body = message.Body },
            data = message.Data,
            android = new
            {
                priority = "normal",
                notification = new { tag = message.Tag, channel_id = AndroidChannelId }
            }
        }
    };

    // Reads only FCM's structured error fields (never the human-readable message).
    private static async Task<(string? ErrorCode, bool TokenFieldViolation)> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("error", out var error)
                || !error.TryGetProperty("details", out var details)
                || details.ValueKind != JsonValueKind.Array)
            {
                return (null, false);
            }

            string? errorCode = null;
            var tokenFieldViolation = false;

            foreach (var detail in details.EnumerateArray())
            {
                if (detail.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (detail.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String)
                {
                    errorCode = code.GetString();
                }

                if (detail.TryGetProperty("fieldViolations", out var violations) && violations.ValueKind == JsonValueKind.Array)
                {
                    tokenFieldViolation |= violations.EnumerateArray().Any(violation =>
                        violation.ValueKind == JsonValueKind.Object
                        && violation.TryGetProperty("field", out var field)
                        && field.ValueKind == JsonValueKind.String
                        && field.GetString() == "message.token");
                }
            }

            return (errorCode, tokenFieldViolation);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or HttpRequestException)
        {
            return (null, false);
        }
    }
}
