using LifeOS.Domain.Users;

namespace LifeOS.Application.Authentication.StartSession;

public sealed class StartSessionHandler
{
    private readonly IUserSessionRepository _sessionRepository;
    private readonly UserSessionOptions _options;
    private readonly TimeProvider _timeProvider;

    public StartSessionHandler(
        IUserSessionRepository sessionRepository,
        UserSessionOptions options,
        TimeProvider timeProvider)
    {
        _sessionRepository = sessionRepository;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<StartSessionResult> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var refreshToken = RefreshTokens.Generate();

        var session = UserSession.Start(
            userId,
            RefreshTokens.Hash(refreshToken),
            _timeProvider.GetUtcNow(),
            _options.RefreshTokenLifetime);

        await _sessionRepository.AddAsync(session, cancellationToken);

        return new StartSessionResult(userId, refreshToken, session.ExpiresAtUtc);
    }
}
