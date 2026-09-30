using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Users;
using LifeOS.Application.Users.GetCurrentUser;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Users;

namespace LifeOS.Application.Onboarding.SetUpFinanceProfile;

// Onboarding step 1: choose the default currency and receive the user's own starter categories.
//
// Order matters for recovery: the missing starter categories are persisted first and the user is
// marked as set up only afterwards. A retry after a partial failure finds the categories already
// present, adds none, and completes the user update.
//
// Idempotent for retries: once set up, the same currency succeeds without writing anything, and a
// different currency is rejected (changing it is not supported).
public sealed class SetUpFinanceProfileHandler
{
    private readonly IUserRepository _userRepository;
    private readonly ICategoryRepository _categoryRepository;
    private readonly TimeProvider _timeProvider;

    public SetUpFinanceProfileHandler(
        IUserRepository userRepository,
        ICategoryRepository categoryRepository,
        TimeProvider timeProvider)
    {
        _userRepository = userRepository;
        _categoryRepository = categoryRepository;
        _timeProvider = timeProvider;
    }

    // Throws ArgumentException for an invalid currency code.
    public async Task<OnboardingResult> HandleAsync(
        Guid userId,
        SetUpFinanceProfileCommand command,
        CancellationToken cancellationToken)
    {
        var currency = User.NormalizeCurrency(command.DefaultCurrency);

        var user = await _userRepository.GetByIdAsync(userId, cancellationToken);

        if (user is null)
        {
            return OnboardingResult.NotFound();
        }

        if (user.OnboardingStatus != OnboardingStatus.PendingFinanceProfile)
        {
            return AlreadySetUp(user, currency);
        }

        var now = _timeProvider.GetUtcNow();

        // A concurrent setup may insert some of the same starter categories first. Re-read and
        // reconcile once; a second conflict is reported instead of retrying further.
        if (!await AddMissingStarterCategoriesAsync(userId, now, cancellationToken)
            && !await AddMissingStarterCategoriesAsync(userId, now, cancellationToken))
        {
            return OnboardingResult.Conflict("The categories changed while setting up the finance profile. Please retry.");
        }

        user.SetUpFinanceProfile(currency, now);

        if (await _userRepository.TryUpdateOnboardingAsync(user, OnboardingStatus.PendingFinanceProfile, cancellationToken))
        {
            return OnboardingResult.Ok(CurrentUser.From(user));
        }

        // A concurrent request set up the profile first: apply the retry rules to its outcome.
        var current = await _userRepository.GetByIdAsync(userId, cancellationToken);

        if (current is null)
        {
            return OnboardingResult.NotFound();
        }

        return current.OnboardingStatus == OnboardingStatus.PendingFinanceProfile
            ? OnboardingResult.Conflict("The onboarding state changed while setting up the finance profile. Please retry.")
            : AlreadySetUp(current, currency);
    }

    private static OnboardingResult AlreadySetUp(User user, string currency) =>
        user.DefaultCurrency == currency
            ? OnboardingResult.Ok(CurrentUser.From(user))
            : OnboardingResult.Invalid(
                "defaultCurrency",
                $"The default currency is already set to {user.DefaultCurrency} and cannot be changed.");

    // Adds only the starter categories this user does not have yet (see StarterCategories.Missing).
    // Returns false when the save hit a sibling-name conflict and persisted nothing.
    private async Task<bool> AddMissingStarterCategoriesAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await _categoryRepository.GetAllAsync(userId, cancellationToken);

        var missing = StarterCategories.Missing(existing)
            .Select(starter => Category.Create(userId, starter.Name, starter.CategoryType, parent: null, now))
            .ToList();

        return missing.Count == 0 || await _categoryRepository.TryAddRangeAsync(missing, cancellationToken);
    }
}
