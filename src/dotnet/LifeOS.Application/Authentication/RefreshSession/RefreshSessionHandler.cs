namespace LifeOS.Application.Authentication.RefreshSession;

public sealed class RefreshSessionHandler
{
    private readonly IUserSessionRepository _sessionRepository;
    private readonly UserSessionOptions _options;
    private readonly TimeProvider _timeProvider;

    public RefreshSessionHandler(
        IUserSessionRepository sessionRepository,
        UserSessionOptions options,
        TimeProvider timeProvider)
    {
        _sessionRepository = sessionRepository;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<RefreshSessionResult> HandleAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return RefreshSessionResult.Rejected();
        }

        var now = _timeProvider.GetUtcNow();
        var session = await _sessionRepository.GetByRefreshTokenHashAsync(RefreshTokens.Hash(refreshToken), cancellationToken);

        if (session is null)
        {
            return RefreshSessionResult.Rejected();
        }

        // Reuse of an already-rotated token: the token may have been stolen, so end the whole chain.
        if (session.IsRotated)
        {
            await _sessionRepository.RevokeFamilyAsync(session.FamilyId, now, cancellationToken);

            return RefreshSessionResult.Rejected();
        }

        if (!session.IsActive(now))
        {
            return RefreshSessionResult.Rejected();
        }

        var newRefreshToken = RefreshTokens.Generate();
        var replacement = session.RotateTo(RefreshTokens.Hash(newRefreshToken), now, _options.RefreshTokenLifetime);

        // Conditional and atomic: a concurrent refresh of the same token loses here and is rejected
        // without revoking the family (no grace window; clients must serialize refreshes).
        if (!await _sessionRepository.TryRotateAsync(session, replacement, cancellationToken))
        {
            return RefreshSessionResult.Rejected();
        }

        return RefreshSessionResult.Refreshed(replacement.UserId, newRefreshToken, replacement.ExpiresAtUtc);
    }
}
