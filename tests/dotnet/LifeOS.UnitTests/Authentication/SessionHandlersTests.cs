using LifeOS.Application.Authentication;
using LifeOS.Application.Authentication.RefreshSession;
using LifeOS.Application.Authentication.RevokeSession;
using LifeOS.Application.Authentication.StartSession;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Authentication;

public class SessionHandlersTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly UserSessionOptions Options = new(TimeSpan.FromDays(30));

    private readonly InMemoryUserSessionRepository _repository = new();

    [Fact]
    public async Task StartSession_StoresOnlyTheHashOfTheReturnedToken()
    {
        var result = await StartAsync(Now);

        var stored = Assert.Single(_repository.Sessions);
        Assert.Equal(UserId, result.UserId);
        Assert.False(string.IsNullOrWhiteSpace(result.RefreshToken));
        Assert.NotEqual(result.RefreshToken, stored.RefreshTokenHash);
        Assert.Equal(RefreshTokens.Hash(result.RefreshToken), stored.RefreshTokenHash);
        Assert.Equal(64, stored.RefreshTokenHash.Length);
        Assert.Equal(Now + Options.RefreshTokenLifetime, result.RefreshTokenExpiresAtUtc);
        Assert.Equal(stored.ExpiresAtUtc, result.RefreshTokenExpiresAtUtc);
    }

    [Fact]
    public async Task StartSession_GeneratesDistinctTokens()
    {
        var first = await StartAsync(Now);
        var second = await StartAsync(Now);

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
    }

    [Fact]
    public async Task StoredHash_FindsTheSession()
    {
        var result = await StartAsync(Now);

        var found = await _repository.GetByRefreshTokenHashAsync(RefreshTokens.Hash(result.RefreshToken), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(UserId, found.UserId);
    }

    [Fact]
    public async Task Refresh_WithActiveToken_RotatesSession()
    {
        var started = await StartAsync(Now);
        var later = Now.AddDays(5);

        var result = await RefreshAsync(later, started.RefreshToken);

        Assert.Equal(RefreshSessionStatus.Refreshed, result.Status);
        Assert.Equal(UserId, result.UserId);
        Assert.NotEqual(started.RefreshToken, result.RefreshToken);
        Assert.Equal(later + Options.RefreshTokenLifetime, result.RefreshTokenExpiresAtUtc);

        var sessions = _repository.Sessions;
        Assert.Equal(2, sessions.Count);
        var old = sessions.Single(session => session.RefreshTokenHash == RefreshTokens.Hash(started.RefreshToken));
        var current = sessions.Single(session => session.RefreshTokenHash == RefreshTokens.Hash(result.RefreshToken!));
        Assert.Equal(later, old.RevokedAtUtc);
        Assert.Equal(current.Id, old.ReplacedBySessionId);
        Assert.Equal(old.FamilyId, current.FamilyId);
        Assert.True(current.IsActive(later));
    }

    [Fact]
    public async Task Refresh_WithRotatedToken_RejectsAndRevokesWholeFamily()
    {
        var started = await StartAsync(Now);
        var rotated = await RefreshAsync(Now.AddMinutes(20), started.RefreshToken);

        // The original token is presented again: possible theft.
        var reuse = await RefreshAsync(Now.AddMinutes(40), started.RefreshToken);

        Assert.Equal(RefreshSessionStatus.Rejected, reuse.Status);
        Assert.All(_repository.Sessions, session => Assert.NotNull(session.RevokedAtUtc));
        // Reported (for the API's security log) as the revoked family, never with a token.
        Assert.Equal(_repository.Sessions[0].FamilyId, reuse.RevokedFamilyId);
        Assert.Null(reuse.RefreshToken);
        Assert.Null(rotated.RevokedFamilyId);

        // The legitimate latest token is now unusable too, but that is not a new reuse.
        var afterReuse = await RefreshAsync(Now.AddMinutes(41), rotated.RefreshToken);
        Assert.Equal(RefreshSessionStatus.Rejected, afterReuse.Status);
        Assert.Null(afterReuse.RevokedFamilyId);
    }

    [Fact]
    public async Task Refresh_WithExpiredToken_IsRejected()
    {
        var started = await StartAsync(Now);

        var result = await RefreshAsync(Now + Options.RefreshTokenLifetime, started.RefreshToken);

        Assert.Equal(RefreshSessionStatus.Rejected, result.Status);
        Assert.Single(_repository.Sessions);
    }

    [Fact]
    public async Task Refresh_WithRevokedToken_IsRejected()
    {
        var started = await StartAsync(Now);
        await RevokeAsync(Now.AddMinutes(1), started.RefreshToken);

        var result = await RefreshAsync(Now.AddMinutes(2), started.RefreshToken);

        Assert.Equal(RefreshSessionStatus.Rejected, result.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-known-token")]
    public async Task Refresh_WithMissingOrUnknownToken_IsRejected(string? token)
    {
        await StartAsync(Now);

        var result = await RefreshAsync(Now.AddMinutes(1), token);

        Assert.Equal(RefreshSessionStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task Refresh_LosingConcurrentRotation_IsRejectedWithoutRevokingFamily()
    {
        var started = await StartAsync(Now);
        var sessionId = Assert.Single(_repository.Sessions).Id;

        // Another request rotates the same row between our read and our conditional update.
        _repository.BeforeRotate = () =>
        {
            _repository.RevokeStored(sessionId, Now.AddMinutes(1));
            _repository.BeforeRotate = null;
        };

        var result = await RefreshAsync(Now.AddMinutes(1), started.RefreshToken);

        Assert.Equal(RefreshSessionStatus.Rejected, result.Status);
        // Nothing inserted, and the family was not revoked as token reuse.
        var stored = Assert.Single(_repository.Sessions);
        Assert.Null(stored.ReplacedBySessionId);
    }

    [Fact]
    public async Task Revoke_RevokesWholeFamily()
    {
        var started = await StartAsync(Now);
        var rotated = await RefreshAsync(Now.AddMinutes(20), started.RefreshToken);

        await RevokeAsync(Now.AddMinutes(30), rotated.RefreshToken);

        Assert.All(_repository.Sessions, session => Assert.NotNull(session.RevokedAtUtc));
        Assert.Equal(RefreshSessionStatus.Rejected, (await RefreshAsync(Now.AddMinutes(31), rotated.RefreshToken)).Status);
    }

    [Fact]
    public async Task Revoke_DoesNotAffectOtherFamilies()
    {
        var first = await StartAsync(Now);
        var second = await StartAsync(Now);

        await RevokeAsync(Now.AddMinutes(1), first.RefreshToken);

        Assert.Equal(RefreshSessionStatus.Refreshed, (await RefreshAsync(Now.AddMinutes(2), second.RefreshToken)).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-known-token")]
    public async Task Revoke_WithMissingOrUnknownToken_DoesNothing(string? token)
    {
        await StartAsync(Now);

        await RevokeAsync(Now.AddMinutes(1), token);

        Assert.Null(Assert.Single(_repository.Sessions).RevokedAtUtc);
    }

    [Fact]
    public async Task Revoke_IsIdempotent()
    {
        var started = await StartAsync(Now);

        await RevokeAsync(Now.AddMinutes(1), started.RefreshToken);
        await RevokeAsync(Now.AddMinutes(2), started.RefreshToken);

        Assert.Equal(Now.AddMinutes(1), Assert.Single(_repository.Sessions).RevokedAtUtc);
    }

    private Task<StartSessionResult> StartAsync(DateTimeOffset now) =>
        new StartSessionHandler(_repository, Options, new FixedTimeProvider(now))
            .HandleAsync(UserId, CancellationToken.None);

    private Task<RefreshSessionResult> RefreshAsync(DateTimeOffset now, string? refreshToken) =>
        new RefreshSessionHandler(_repository, Options, new FixedTimeProvider(now))
            .HandleAsync(refreshToken, CancellationToken.None);

    private Task RevokeAsync(DateTimeOffset now, string? refreshToken) =>
        new RevokeSessionHandler(_repository, new FixedTimeProvider(now))
            .HandleAsync(refreshToken, CancellationToken.None);
}
