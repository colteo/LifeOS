using LifeOS.Infrastructure.Interpretation;
using LifeOS.Infrastructure.Nutrition;

namespace LifeOS.Api.Assistant;

// AI-007 F3: whether LifeOS Assistant may ask the Python AI service to interpret messages.
//   AssistantAi:Enabled         optional, true/false (default false). false: no message leaves LifeOS and every
//                               turn fails with assistant_unavailable. true (explicit opt-in, AI-007 release)
//                               requires the AI service (NutritionAi:BaseUrl and ServiceKey); the provider and
//                               its key are configured only on the AI service.
//   AssistantAi:TimeoutSeconds  optional, 1-75 (default 45): within the 90 s turn lease minus the 15 s needed
//                               to store the outcome.
// A present but invalid value stops the API at startup. Error messages name the setting, never its value.
public static class AssistantAiConfiguration
{
    public const string EnabledKey = "AssistantAi:Enabled";
    public const string TimeoutSecondsKey = "AssistantAi:TimeoutSeconds";

    public static AssistantAiOptions Read(IConfiguration configuration, NutritionAiOptions aiService)
    {
        var enabledText = configuration[EnabledKey];
        var timeoutText = configuration[TimeoutSecondsKey];
        var enabled = false;
        var timeout = AssistantAiOptions.DefaultTimeout;

        if (!string.IsNullOrWhiteSpace(enabledText) && !bool.TryParse(enabledText.Trim(), out enabled))
        {
            throw new InvalidOperationException($"{EnabledKey} must be true or false.");
        }

        if (!string.IsNullOrWhiteSpace(timeoutText))
        {
            if (!int.TryParse(timeoutText, out var seconds) || seconds < 1 || TimeSpan.FromSeconds(seconds) > AssistantAiOptions.MaxTimeout)
            {
                throw new InvalidOperationException(
                    $"{TimeoutSecondsKey} must be a whole number of seconds between 1 and {(int)AssistantAiOptions.MaxTimeout.TotalSeconds}.");
            }

            timeout = TimeSpan.FromSeconds(seconds);
        }

        if (enabled && aiService.BaseUrl is null)
        {
            throw new InvalidOperationException($"{EnabledKey} requires {LifeOS.Api.Nutrition.NutritionAiConfiguration.BaseUrlKey}.");
        }

        return new AssistantAiOptions(enabled, timeout);
    }
}
