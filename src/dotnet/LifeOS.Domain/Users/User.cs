namespace LifeOS.Domain.Users;

// A LifeOS user. How the user signs in is modelled separately by ExternalIdentity.
public sealed class User
{
    private User(
        Guid id,
        string? displayName,
        string? email,
        OnboardingStatus onboardingStatus,
        string? defaultCurrency,
        DateTimeOffset? starterCategoriesInitializedAtUtc,
        DateTimeOffset? onboardingCompletedAtUtc,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        DisplayName = displayName;
        Email = email;
        OnboardingStatus = onboardingStatus;
        DefaultCurrency = defaultCurrency;
        StarterCategoriesInitializedAtUtc = starterCategoriesInitializedAtUtc;
        OnboardingCompletedAtUtc = onboardingCompletedAtUtc;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    public string? DisplayName { get; }

    // Informational profile data only: never an identity key and not unique.
    public string? Email { get; }

    public OnboardingStatus OnboardingStatus { get; }

    public string? DefaultCurrency { get; }

    public DateTimeOffset? StarterCategoriesInitializedAtUtc { get; }

    public DateTimeOffset? OnboardingCompletedAtUtc { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    // Display name and email are taken from the provider once, when the user is created.
    public static User CreateFromExternalIdentity(string? displayName, string? email, DateTimeOffset createdAtUtc) =>
        new(
            Guid.CreateVersion7(),
            NormalizeOptional(displayName),
            NormalizeOptional(email),
            OnboardingStatus.PendingFinanceProfile,
            defaultCurrency: null,
            starterCategoriesInitializedAtUtc: null,
            onboardingCompletedAtUtc: null,
            createdAtUtc.ToUniversalTime());

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
