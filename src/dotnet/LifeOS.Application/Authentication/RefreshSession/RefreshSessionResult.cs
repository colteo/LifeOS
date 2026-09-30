namespace LifeOS.Application.Authentication.RefreshSession;

public enum RefreshSessionStatus
{
    Refreshed,
    Rejected
}

// The reason for a rejection (unknown, expired, revoked, reused) is deliberately not exposed.
public sealed record RefreshSessionResult(
    RefreshSessionStatus Status,
    Guid? UserId,
    string? RefreshToken,
    DateTimeOffset? RefreshTokenExpiresAtUtc)
{
    public static RefreshSessionResult Refreshed(Guid userId, string refreshToken, DateTimeOffset expiresAtUtc) =>
        new(RefreshSessionStatus.Refreshed, userId, refreshToken, expiresAtUtc);

    public static RefreshSessionResult Rejected() =>
        new(RefreshSessionStatus.Rejected, null, null, null);
}
