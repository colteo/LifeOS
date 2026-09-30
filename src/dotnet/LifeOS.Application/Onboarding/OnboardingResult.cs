using LifeOS.Application.Users.GetCurrentUser;

namespace LifeOS.Application.Onboarding;

public enum OnboardingResultStatus
{
    Ok,
    NotFound,
    Invalid,
    Conflict
}

// Result of an onboarding step. On Ok, User is the user's profile after the step.
public sealed record OnboardingResult(
    OnboardingResultStatus Status,
    CurrentUser? User,
    string? Field,
    string? Message)
{
    public static OnboardingResult Ok(CurrentUser user) =>
        new(OnboardingResultStatus.Ok, user, null, null);

    public static OnboardingResult NotFound() =>
        new(OnboardingResultStatus.NotFound, null, null, "The user does not exist.");

    public static OnboardingResult Invalid(string field, string message) =>
        new(OnboardingResultStatus.Invalid, null, field, message);

    public static OnboardingResult Conflict(string message) =>
        new(OnboardingResultStatus.Conflict, null, null, message);
}
