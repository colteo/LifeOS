namespace LifeOS.Application.Authentication.RevokeSession;

// Sign-out: ends the whole session family of the presented refresh token.
// Idempotent, and silent for unknown tokens so callers learn nothing about token validity.
public sealed class RevokeSessionHandler
{
    private readonly IUserSessionRepository _sessionRepository;
    private readonly TimeProvider _timeProvider;

    public RevokeSessionHandler(IUserSessionRepository sessionRepository, TimeProvider timeProvider)
    {
        _sessionRepository = sessionRepository;
        _timeProvider = timeProvider;
    }

    public async Task HandleAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var session = await _sessionRepository.GetByRefreshTokenHashAsync(RefreshTokens.Hash(refreshToken), cancellationToken);

        if (session is not null)
        {
            await _sessionRepository.RevokeFamilyAsync(session.FamilyId, _timeProvider.GetUtcNow(), cancellationToken);
        }
    }
}
