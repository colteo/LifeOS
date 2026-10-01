namespace LifeOS.Api.Authentication;

// The Google accounts allowed to sign in to LifeOS, from "Authentication:Google:AllowedEmails"
// (a list: Authentication__Google__AllowedEmails__0, __1, … as environment variables).
//
// Outside Development the list is mandatory: LifeOS is not open for registration, so an empty or
// missing list fails startup instead of letting any Google account create a user. In Development an
// empty list keeps sign-in open, as before. Only an email the provider reports as verified can match;
// entries are trimmed and compared case-insensitively.
public sealed class GoogleAccountAllowlist
{
    public const string AllowedEmailsKey = "Authentication:Google:AllowedEmails";

    // null: open (Development without a list).
    private readonly HashSet<string>? _emails;

    private GoogleAccountAllowlist(HashSet<string>? emails)
    {
        _emails = emails;
    }

    public bool IsOpen => _emails is null;

    public static GoogleAccountAllowlist FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        var emails = configuration.GetSection(AllowedEmailsKey).GetChildren()
            .Select(entry => entry.Value?.Trim())
            .Where(email => !string.IsNullOrEmpty(email))
            .Select(email => email!)
            .ToList();

        // The values are never part of the message.
        if (emails.Any(email => !IsEmailAddress(email)))
        {
            throw new InvalidOperationException($"Every {AllowedEmailsKey} entry must be an email address.");
        }

        if (emails.Count > 0)
        {
            return new GoogleAccountAllowlist(new HashSet<string>(emails, StringComparer.OrdinalIgnoreCase));
        }

        if (environment.IsDevelopment())
        {
            return new GoogleAccountAllowlist(null);
        }

        throw new InvalidOperationException(
            $"{AllowedEmailsKey} must list at least one Google account in the '{environment.EnvironmentName}' environment "
            + "(e.g. Authentication__Google__AllowedEmails__0). Open sign-up is allowed only in Development.");
    }

    public bool Allows(string? email, bool emailVerified) =>
        _emails is null
        || (emailVerified && !string.IsNullOrWhiteSpace(email) && _emails.Contains(email.Trim()));

    private static bool IsEmailAddress(string value)
    {
        var at = value.IndexOf('@');

        return at > 0 && at == value.LastIndexOf('@') && at < value.Length - 1 && !value.Any(char.IsWhiteSpace);
    }
}
