namespace LifeOS.Contracts.Users;

public sealed record MeResponse(
    Guid UserId,
    string? DisplayName,
    string? Email,
    string OnboardingStatus,
    string? DefaultCurrency);
