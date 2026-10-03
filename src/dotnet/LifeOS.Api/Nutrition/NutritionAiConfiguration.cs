using LifeOS.Infrastructure.Nutrition;

namespace LifeOS.Api.Nutrition;

// Reads the Python AI service location (ADR-011):
//   NutritionAi:BaseUrl         absolute http(s) URL, e.g. http://127.0.0.1:8000 (absent: estimation disabled)
//   NutritionAi:TimeoutSeconds  optional, 1-300 (default 60)
// A present but invalid value stops the API at startup rather than failing on the first estimate.
public static class NutritionAiConfiguration
{
    public const string BaseUrlKey = "NutritionAi:BaseUrl";
    public const string TimeoutSecondsKey = "NutritionAi:TimeoutSeconds";

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

        // A trailing slash keeps relative request paths under any path prefix of the base URL.
        return new NutritionAiOptions(uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/"), timeout);
    }
}
