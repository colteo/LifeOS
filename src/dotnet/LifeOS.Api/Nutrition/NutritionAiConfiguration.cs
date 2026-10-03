using LifeOS.Infrastructure.Nutrition;

namespace LifeOS.Api.Nutrition;

// Reads the Python AI service location (ADR-011) and its service key (PROD-AI-001):
//   NutritionAi:BaseUrl         absolute URL (absent: estimation disabled). https, or plain http only to a
//                               loopback host (local development, e.g. http://127.0.0.1:8000).
//   NutritionAi:ServiceKey      secret shared with the AI service (LIFEOS_AI_SERVICE_KEY), sent as a Bearer
//                               token. Required whenever BaseUrl is set: at least 32 visible ASCII
//                               characters, no spaces. Environment / User Secrets only, never appsettings.
//   NutritionAi:TimeoutSeconds  optional, 1-300 (default 60)
// A present but invalid value stops the API at startup rather than failing on the first estimate.
// Error messages name the setting, never its value.
public static class NutritionAiConfiguration
{
    public const string BaseUrlKey = "NutritionAi:BaseUrl";
    public const string ServiceKeyKey = "NutritionAi:ServiceKey";
    public const string TimeoutSecondsKey = "NutritionAi:TimeoutSeconds";
    public const int MinimumServiceKeyLength = 32;

    public static NutritionAiOptions Read(IConfiguration configuration)
    {
        var baseUrl = configuration[BaseUrlKey];
        var timeoutText = configuration[TimeoutSecondsKey];
        var timeout = NutritionAiOptions.DefaultTimeout;

        if (!string.IsNullOrWhiteSpace(timeoutText))
        {
            if (!int.TryParse(timeoutText, out var seconds) || seconds is < 1 or > 300)
            {
                throw new InvalidOperationException($"{TimeoutSecondsKey} must be a whole number of seconds between 1 and 300.");
            }

            timeout = TimeSpan.FromSeconds(seconds);
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return NutritionAiOptions.Disabled with { Timeout = timeout };
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"{BaseUrlKey} must be an absolute http or https URL.");
        }

        // The service key travels in every request: never over plain HTTP beyond this machine.
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            throw new InvalidOperationException($"{BaseUrlKey} must use https unless it points to a loopback address.");
        }

        var serviceKey = configuration[ServiceKeyKey]?.Trim();

        if (string.IsNullOrEmpty(serviceKey))
        {
            throw new InvalidOperationException($"{ServiceKeyKey} is required when {BaseUrlKey} is configured.");
        }

        if (serviceKey.Length < MinimumServiceKeyLength || serviceKey.Any(character => character is < '!' or > '~'))
        {
            throw new InvalidOperationException(
                $"{ServiceKeyKey} must be at least {MinimumServiceKeyLength} visible ASCII characters without spaces.");
        }

        // A trailing slash keeps relative request paths under any path prefix of the base URL.
        return new NutritionAiOptions(uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/"), timeout, serviceKey);
    }
}
