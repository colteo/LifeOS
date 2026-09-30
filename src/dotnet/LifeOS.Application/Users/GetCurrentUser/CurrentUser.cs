using LifeOS.Domain.Users;

namespace LifeOS.Application.Users.GetCurrentUser;

public sealed record CurrentUser(
    Guid UserId,
    string? DisplayName,
    string? Email,
    OnboardingStatus OnboardingStatus,
    string? DefaultCurrency);
