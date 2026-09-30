using LifeOS.Application.Finance.Accounts;
using LifeOS.Application.Users;
using LifeOS.Application.Users.GetCurrentUser;
using LifeOS.Domain.Users;

namespace LifeOS.Application.Onboarding.CompleteOnboarding;

// Final onboarding step: requires the finance profile and at least one account owned by the user.
// Idempotent: completing an already completed onboarding succeeds without writing anything.
public sealed class CompleteOnboardingHandler
{
    private readonly IUserRepository _userRepository;
    private readonly IAccountRepository _accountRepository;
    private readonly TimeProvider _timeProvider;

    public CompleteOnboardingHandler(
        IUserRepository userRepository,
        IAccountRepository accountRepository,
        TimeProvider timeProvider)
    {
        _userRepository = userRepository;
        _accountRepository = accountRepository;
        _timeProvider = timeProvider;
    }

    public async Task<OnboardingResult> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(userId, cancellationToken);

        if (user is null)
        {
            return OnboardingResult.NotFound();
        }

        if (user.OnboardingStatus == OnboardingStatus.Completed)
        {
            return OnboardingResult.Ok(CurrentUser.From(user));
        }

        if (user.OnboardingStatus == OnboardingStatus.PendingFinanceProfile)
        {
            return OnboardingResult.Invalid(
                "onboardingStatus",
                "Set up the finance profile before completing onboarding.");
        }

        if (!await _accountRepository.AnyAsync(userId, cancellationToken))
        {
            return OnboardingResult.Invalid(
                "account",
                "Create a first account before completing onboarding.");
        }

        user.CompleteOnboarding(_timeProvider.GetUtcNow());

        if (await _userRepository.TryUpdateOnboardingAsync(user, OnboardingStatus.PendingFirstAccount, cancellationToken))
        {
            return OnboardingResult.Ok(CurrentUser.From(user));
        }

        // A concurrent request changed the state first; completing twice is still a success.
        var current = await _userRepository.GetByIdAsync(userId, cancellationToken);

        if (current is null)
        {
            return OnboardingResult.NotFound();
        }

        return current.OnboardingStatus == OnboardingStatus.Completed
            ? OnboardingResult.Ok(CurrentUser.From(current))
            : OnboardingResult.Conflict("The onboarding state changed while completing onboarding. Please retry.");
    }
}
