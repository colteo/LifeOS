namespace LifeOS.Application.Authentication;

// Refresh-token lifetime. Sliding: each rotation starts a new full lifetime.
public sealed record UserSessionOptions(TimeSpan RefreshTokenLifetime);
