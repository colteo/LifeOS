namespace LifeOS.Application.Authentication.RefreshSession;

public enum RefreshSessionStatus
{
    Refreshed,
    Rejected
}

// The reason for a rejection (unknown, expired, revoked, reused) is deliberately not exposed to the
// client. RevokedFamilyId is set only when reuse of a rotated token revoked its session family, so the
// API can log that security event (the id is not a credential).
public sealed record RefreshSessionResult(
    RefreshSessionStatus Status,
    Guid? UserId,
    string? RefreshToken,
    DateTimeOffset? RefreshTokenExpiresAtUtc,
    Guid? RevokedFamilyId = null)
{
    public static RefreshSessionResult Refreshed(Guid userId, string refreshToken, DateTimeOffset expiresAtUtc) =>
        new(RefreshSessionStatus.Refreshed, userId, refreshToken, expiresAtUtc);

    public static RefreshSessionResult Rejected() =>
        new(RefreshSessionStatus.Rejected, null, null, null);

    public static RefreshSessionResult RejectedReuse(Guid revokedFamilyId) =>
        new(RefreshSessionStatus.Rejected, null, null, null, revokedFamilyId);
}
