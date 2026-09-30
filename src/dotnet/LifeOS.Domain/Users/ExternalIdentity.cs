namespace LifeOS.Domain.Users;

// Links a LifeOS user to an account at an external identity provider.
// The identity key is (Provider, Subject); email is never part of it.
public sealed class ExternalIdentity
{
    public const int MaxProviderLength = 32;
    public const int MaxSubjectLength = 255;

    private ExternalIdentity(
        Guid id,
        Guid userId,
        string provider,
        string subject,
        string? emailAtSignIn,
        DateTimeOffset createdAtUtc,
        DateTimeOffset lastSignInAtUtc)
    {
        Id = id;
        UserId = userId;
        Provider = provider;
        Subject = subject;
        EmailAtSignIn = emailAtSignIn;
        CreatedAtUtc = createdAtUtc;
        LastSignInAtUtc = lastSignInAtUtc;
    }

    public Guid Id { get; }

    public Guid UserId { get; }

    public string Provider { get; }

    public string Subject { get; }

    // Snapshot of the email the provider reported at sign-in; informational only.
    public string? EmailAtSignIn { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset LastSignInAtUtc { get; private set; }

    public static ExternalIdentity Create(
        Guid userId,
        string provider,
        string subject,
        string? emailAtSignIn,
        DateTimeOffset signedInAtUtc)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        var signedInAt = signedInAtUtc.ToUniversalTime();

        return new ExternalIdentity(
            Guid.CreateVersion7(),
            userId,
            NormalizeProvider(provider),
            NormalizeSubject(subject),
            NormalizeEmail(emailAtSignIn),
            signedInAt,
            signedInAt);
    }

    // A missing email keeps the previous snapshot instead of erasing it.
    public void RecordSignIn(string? emailAtSignIn, DateTimeOffset signedInAtUtc)
    {
        if (NormalizeEmail(emailAtSignIn) is { } email)
        {
            EmailAtSignIn = email;
        }

        LastSignInAtUtc = signedInAtUtc.ToUniversalTime();
    }

    public static string NormalizeProvider(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        var normalized = provider.Trim().ToLowerInvariant();

        if (normalized.Length > MaxProviderLength)
        {
            throw new ArgumentException(
                $"Provider must be at most {MaxProviderLength} characters.", nameof(provider));
        }

        return normalized;
    }

    // Provider subjects are opaque and case-sensitive: only surrounding whitespace is removed.
    public static string NormalizeSubject(string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var normalized = subject.Trim();

        if (normalized.Length > MaxSubjectLength)
        {
            throw new ArgumentException(
                $"Subject must be at most {MaxSubjectLength} characters.", nameof(subject));
        }

        return normalized;
    }

    private static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim();
}
