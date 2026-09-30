using LifeOS.Application.Finance.Categories;
using LifeOS.Application.Users;
using LifeOS.Application.Users.GetCurrentUser;
using LifeOS.Domain.Users;

namespace LifeOS.Application.Onboarding.SetUpFinanceProfile;

// Onboarding step 1: choose the default currency and receive the user's own starter category tree.
//
// Order matters for recovery: the missing part of the starter tree is persisted first and the user is
// marked as set up only afterwards, so a partial tree never completes this step. A retry after a
// partial failure finds the categories already present, adds only what is missing, and completes the
// user update.
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

        // A concurrent setup may insert the same starter categories (conflict) or lose a deadlock to
        // this one. Bounded, no loop:
        //   1. insert the missing part of the tree, parents and children in one atomic batch;
        //   2. on failure, re-read and retry only what is still missing, once;
        //   3. if that also fails, re-read one final time: the concurrent setup may have committed the
        //      complete tree by now, which is success; anything still missing is a 409.
        if (!await AddMissingStarterCategoriesAsync(userId, now, cancellationToken)
            && !await AddMissingStarterCategoriesAsync(userId, now, cancellationToken)
            && !await HasCompleteStarterTreeAsync(userId, cancellationToken))
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

    private async Task<bool> HasCompleteStarterTreeAsync(Guid userId, CancellationToken cancellationToken) =>
        StarterCategories.IsComplete(userId, await _categoryRepository.GetAllAsync(userId, cancellationToken));

    // Adds only the starter categories this user does not have yet (see StarterCategories.CreateMissing)
    // in a single save: new parents and their children commit together or not at all. Returns false
    // when the save failed on a concurrent write and persisted nothing.
    private async Task<bool> AddMissingStarterCategoriesAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await _categoryRepository.GetAllAsync(userId, cancellationToken);

        var missing = StarterCategories.CreateMissing(userId, existing, now);

        return missing.Count == 0 || await _categoryRepository.TryAddRangeAsync(missing, cancellationToken);
    }
}
