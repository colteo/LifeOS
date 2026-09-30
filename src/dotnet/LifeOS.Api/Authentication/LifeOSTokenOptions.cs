namespace LifeOS.Api.Authentication;

// Settings for LifeOS-issued tokens, read from "Authentication:LifeOS".
// Issuer, audience and lifetimes live in appsettings.json; the signing key only in User Secrets
// (or another secret store): Base64 of at least 32 cryptographically random bytes.
public sealed class LifeOSTokenOptions
{
    public const string SectionName = "Authentication:LifeOS";

    private const int MinimumSigningKeyBytes = 32;

    private LifeOSTokenOptions(
        string issuer,
        string audience,
        byte[] signingKey,
        TimeSpan accessTokenLifetime,
        TimeSpan refreshTokenLifetime)
    {
        Issuer = issuer;
        Audience = audience;
        SigningKey = signingKey;
        AccessTokenLifetime = accessTokenLifetime;
        RefreshTokenLifetime = refreshTokenLifetime;
    }

    public string Issuer { get; }

    public string Audience { get; }

    public byte[] SigningKey { get; }

    public TimeSpan AccessTokenLifetime { get; }

    public TimeSpan RefreshTokenLifetime { get; }

    // Fails startup on missing or invalid settings rather than running with weak tokens.
    public static LifeOSTokenOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        var issuer = Required(section, "Issuer");
        var audience = Required(section, "Audience");
        var signingKey = DecodeSigningKey(Required(section, "SigningKey"));
        var accessTokenLifetime = PositiveTimeSpan(section, "AccessTokenLifetime");
        var refreshTokenLifetime = PositiveTimeSpan(section, "RefreshTokenLifetime");

        return new LifeOSTokenOptions(issuer, audience, signingKey, accessTokenLifetime, refreshTokenLifetime);
    }

    private static byte[] DecodeSigningKey(string value)
    {
        byte[] key;

        try
        {
            key = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{SectionName}:SigningKey must be Base64.");
        }

        if (key.Length < MinimumSigningKeyBytes)
        {
            throw new InvalidOperationException(
                $"{SectionName}:SigningKey must decode to at least {MinimumSigningKeyBytes} bytes.");
        }

        return key;
    }

    private static string Required(IConfigurationSection section, string key)
    {
        var value = section[key];

        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{SectionName}:{key} is required.")
            : value;
    }

    private static TimeSpan PositiveTimeSpan(IConfigurationSection section, string key)
    {
        var value = section.GetValue<TimeSpan?>(key);

        return value is { } lifetime && lifetime > TimeSpan.Zero
            ? lifetime
            : throw new InvalidOperationException($"{SectionName}:{key} must be a positive time span.");
    }
}
