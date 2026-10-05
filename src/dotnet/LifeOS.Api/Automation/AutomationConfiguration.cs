namespace LifeOS.Api.Automation;

public sealed record AutomationOptions(string TickKey);

// AUTO-001 §13 and §6. Automation is enabled only when Automation:TickKey (environment variable
// Automation__TickKey) is configured; otherwise the tick endpoint is not mapped and the rest of
// LifeOS works as before. A configured key that is too weak, or an image without the IANA time zone
// database, fails startup instead of running a broken scheduler.
public static class AutomationConfiguration
{
    public const string TickKeyKey = "Automation:TickKey";
    public const int MinimumTickKeyLength = 32;

    // Any zone a user is likely to have; resolving it proves tzdata is present (AUTO-001 §6).
    public const string ReferenceTimeZoneId = "Europe/Rome";

    // Null when automation is disabled.
    public static AutomationOptions? Read(IConfiguration configuration) =>
        Read(configuration, TimeZoneInfo.FindSystemTimeZoneById);

    public static AutomationOptions? Read(IConfiguration configuration, Func<string, TimeZoneInfo> findTimeZone)
    {
        var tickKey = configuration[TickKeyKey]?.Trim();

        if (string.IsNullOrEmpty(tickKey))
        {
            return null;
        }

        // The value itself is never part of the message.
        if (tickKey.Length < MinimumTickKeyLength || tickKey.Any(character => character is < '!' or > '~'))
        {
            throw new InvalidOperationException(
                $"{TickKeyKey} must be at least {MinimumTickKeyLength} visible ASCII characters without spaces.");
        }

        EnsureTimeZoneData(findTimeZone);

        return new AutomationOptions(tickKey);
    }

    public static void EnsureTimeZoneData(Func<string, TimeZoneInfo> findTimeZone)
    {
        TimeZoneInfo zone;

        try
        {
            zone = findTimeZone(ReferenceTimeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"Automation is enabled but the IANA time zone database is unavailable: '{ReferenceTimeZoneId}' cannot be resolved.",
                exception);
        }

        if (!zone.HasIanaId)
        {
            throw new InvalidOperationException(
                $"Automation is enabled but '{ReferenceTimeZoneId}' does not resolve to an IANA time zone.");
        }
    }
}
