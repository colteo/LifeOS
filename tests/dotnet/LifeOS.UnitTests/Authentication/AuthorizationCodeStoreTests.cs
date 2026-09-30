using LifeOS.Api.Authentication;

namespace LifeOS.UnitTests.Authentication;

public class AuthorizationCodeStoreTests
{
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero));
    private readonly AuthorizationCodeStore _store;

    public AuthorizationCodeStoreTests()
    {
        _store = new AuthorizationCodeStore(_clock);
    }

    [Fact]
    public void Consume_ReturnsTheGrantOnce()
    {
        var userId = Guid.CreateVersion7();
        var code = _store.Create(userId, Challenge, AppCallbacks.Auth);

        var grant = _store.TryConsume(code);

        Assert.Equal(new AuthorizationCodeGrant(userId, Challenge, AppCallbacks.Auth), grant);
        Assert.Null(_store.TryConsume(code));
    }

    [Fact]
    public void Codes_AreRandomAndUrlSafe()
    {
        var first = _store.Create(Guid.CreateVersion7(), Challenge, AppCallbacks.Auth);
        var second = _store.Create(Guid.CreateVersion7(), Challenge, AppCallbacks.Auth);

        Assert.NotEqual(first, second);
        Assert.Equal(43, first.Length);
        Assert.All(first, character => Assert.True(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
    }

    [Fact]
    public void Consume_AfterLifetime_ReturnsNullAndRemovesTheCode()
    {
        var code = _store.Create(Guid.CreateVersion7(), Challenge, AppCallbacks.Auth);

        _clock.Advance(AuthorizationCodeStore.Lifetime);

        Assert.Null(_store.TryConsume(code));
        _clock.Advance(-AuthorizationCodeStore.Lifetime);
        Assert.Null(_store.TryConsume(code));
    }

    [Fact]
    public void Consume_JustBeforeExpiry_Succeeds()
    {
        var code = _store.Create(Guid.CreateVersion7(), Challenge, AppCallbacks.Auth);

        _clock.Advance(AuthorizationCodeStore.Lifetime - TimeSpan.FromMilliseconds(1));

        Assert.NotNull(_store.TryConsume(code));
    }

    [Fact]
    public void Consume_UnknownCode_ReturnsNull()
    {
        _store.Create(Guid.CreateVersion7(), Challenge, AppCallbacks.Auth);

        Assert.Null(_store.TryConsume("not-a-code"));
    }

    [Fact]
    public async Task ConcurrentConsumes_ExactlyOneSucceeds()
    {
        var code = _store.Create(Guid.CreateVersion7(), Challenge, AppCallbacks.Auth);
        using var start = new ManualResetEventSlim();

        var attempts = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return _store.TryConsume(code);
            }))
            .ToList();

        start.Set();
        var grants = await Task.WhenAll(attempts);

        Assert.Single(grants, grant => grant is not null);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
