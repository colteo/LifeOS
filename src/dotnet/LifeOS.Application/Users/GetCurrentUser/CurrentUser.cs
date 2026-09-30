using LifeOS.Domain.Users;

namespace LifeOS.Application.Users.GetCurrentUser;

public sealed record CurrentUser(
    Guid UserId,
    string? DisplayName,
    string? Email,
    OnboardingStatus OnboardingStatus,
    string? DefaultCurrency)
{
    public static CurrentUser From(User user) =>
        new(user.Id, user.DisplayName, user.Email, user.OnboardingStatus, user.DefaultCurrency);
}
