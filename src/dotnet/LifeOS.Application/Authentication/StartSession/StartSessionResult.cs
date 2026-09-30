namespace LifeOS.Application.Authentication.StartSession;

// RefreshToken is the raw token: it is returned to the caller once and never stored.
public sealed record StartSessionResult(Guid UserId, string RefreshToken, DateTimeOffset RefreshTokenExpiresAtUtc);
