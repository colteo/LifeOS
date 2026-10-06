using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LifeOS.Infrastructure.Notifications.Fcm;

namespace LifeOS.Api.Notifications;

// AUTO-001 open question 3, decided in WP3B. The same rules in every environment:
// - neither value configured → push disabled (no sender, dispatch disabled), LifeOS starts normally;
// - only one configured, or either malformed → startup fails. A partially configured provider is
//   never silently disabled.
// The service-account JSON is configured Base64-encoded (Notifications__Fcm__ServiceAccountJson).
// Its content is never logged nor quoted in an error. Whether it is a service-account key is checked
// by Google.Apis.Auth's typed loader when Infrastructure registers the sender (also at startup).
public static partial class PushNotificationsConfiguration
{
    public const string ProjectIdKey = "Notifications:Fcm:ProjectId";
    public const string ServiceAccountJsonKey = "Notifications:Fcm:ServiceAccountJson";

    public static FcmOptions? ReadFcm(IConfiguration configuration)
    {
        var projectId = configuration[ProjectIdKey]?.Trim();
        var encodedJson = configuration[ServiceAccountJsonKey]?.Trim();

        if (string.IsNullOrEmpty(projectId) && string.IsNullOrEmpty(encodedJson))
        {
            return null;
        }

        if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(encodedJson))
        {
            throw new InvalidOperationException(
                $"{ProjectIdKey} and {ServiceAccountJsonKey} must be configured together (or both left empty to disable push).");
        }

        if (!ProjectIdPattern().IsMatch(projectId))
        {
            throw new InvalidOperationException($"{ProjectIdKey} must be a Google Cloud project id (6–30 lowercase letters, digits or hyphens).");
        }

        var bytes = new byte[encodedJson.Length];

        if (!Convert.TryFromBase64String(encodedJson, bytes, out var length))
        {
            throw new InvalidOperationException($"{ServiceAccountJsonKey} must be the Base64-encoded service-account JSON.");
        }

        string json;

        try
        {
            json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes, 0, length);

            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException)
        {
            // No inner exception: parser messages can quote the content.
            throw new InvalidOperationException($"{ServiceAccountJsonKey} must decode to a JSON service-account key.");
        }

        return new FcmOptions(projectId, json);
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{4,28}[a-z0-9]$")]
    private static partial Regex ProjectIdPattern();
}
