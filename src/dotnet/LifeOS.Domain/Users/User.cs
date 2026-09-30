namespace LifeOS.Domain.Users;

// A LifeOS user. How the user signs in is modelled separately by ExternalIdentity.
public sealed class User
{
    private const int CurrencyCodeLength = 3;

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

    public OnboardingStatus OnboardingStatus { get; private set; }

    public string? DefaultCurrency { get; private set; }

    public DateTimeOffset? StarterCategoriesInitializedAtUtc { get; private set; }

    public DateTimeOffset? OnboardingCompletedAtUtc { get; private set; }

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

    // Onboarding step 1: the default currency is chosen and the starter categories exist.
    // The caller persists the starter categories before calling this.
    public void SetUpFinanceProfile(string defaultCurrency, DateTimeOffset starterCategoriesInitializedAtUtc)
    {
        EnsureStatus(OnboardingStatus.PendingFinanceProfile, nameof(SetUpFinanceProfile));

        DefaultCurrency = NormalizeCurrency(defaultCurrency);
        StarterCategoriesInitializedAtUtc = starterCategoriesInitializedAtUtc.ToUniversalTime();
        OnboardingStatus = OnboardingStatus.PendingFirstAccount;
    }

    // Onboarding step 2. That a first account exists is checked by the caller (Application).
    public void CompleteOnboarding(DateTimeOffset completedAtUtc)
    {
        EnsureStatus(OnboardingStatus.PendingFirstAccount, nameof(CompleteOnboarding));

        OnboardingCompletedAtUtc = completedAtUtc.ToUniversalTime();
        OnboardingStatus = OnboardingStatus.Completed;
    }

    // Same convention as account and transaction currencies: trimmed, 3 ASCII letters, uppercase.
    public static string NormalizeCurrency(string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        var normalized = currency.Trim().ToUpperInvariant();

        if (normalized.Length != CurrencyCodeLength || !normalized.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException("Currency must be a 3-letter ISO-style code.", nameof(currency));
        }

        return normalized;
    }

    private void EnsureStatus(OnboardingStatus required, string transition)
    {
        if (OnboardingStatus != required)
        {
            throw new InvalidOperationException(
                $"{transition} requires onboarding status {required}, but the user is {OnboardingStatus}.");
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
