using LifeOS.Domain.Users;

namespace LifeOS.UnitTests.Users;

public class UserSessionTests
{
    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string NextHash = "0000000000000000000000000000000000000000000000000000000000000002";

    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    [Fact]
    public void Start_CreatesActiveSessionStartingItsOwnFamily()
    {
        var session = UserSession.Start(UserId, Hash, Now, Lifetime);

        Assert.Equal(7, session.Id.Version);
        Assert.Equal(UserId, session.UserId);
        Assert.Equal(session.Id, session.FamilyId);
        Assert.Equal(Hash, session.RefreshTokenHash);
        Assert.Equal(Now, session.CreatedAtUtc);
        Assert.Equal(Now + Lifetime, session.ExpiresAtUtc);
        Assert.Null(session.RevokedAtUtc);
        Assert.Null(session.ReplacedBySessionId);
        Assert.True(session.IsActive(Now));
    }

    [Fact]
    public void Start_WithEmptyUserId_Throws()
    {
        Assert.Throws<ArgumentException>(() => UserSession.Start(Guid.Empty, Hash, Now, Lifetime));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Start_WithBlankHash_Throws(string? hash)
    {
        Assert.ThrowsAny<ArgumentException>(() => UserSession.Start(UserId, hash!, Now, Lifetime));
    }

    [Fact]
    public void Start_WithNonPositiveLifetime_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UserSession.Start(UserId, Hash, Now, TimeSpan.Zero));
    }

    [Fact]
    public void Start_NormalizesTimesToUtc()
    {
        var local = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(2));

        var session = UserSession.Start(UserId, Hash, local, Lifetime);

        Assert.Equal(TimeSpan.Zero, session.CreatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, session.ExpiresAtUtc.Offset);
    }

    [Fact]
    public void IsActive_IsFalseAtAndAfterExpiry()
    {
        var session = UserSession.Start(UserId, Hash, Now, Lifetime);

        Assert.True(session.IsActive(Now + Lifetime - TimeSpan.FromTicks(1)));
        Assert.False(session.IsActive(Now + Lifetime));
    }

    [Fact]
    public void RotateTo_RevokesAndLinksToReplacementInSameFamily()
    {
        var session = UserSession.Start(UserId, Hash, Now, Lifetime);
        var later = Now.AddDays(10);

        var replacement = session.RotateTo(NextHash, later, Lifetime);

        Assert.Equal(later, session.RevokedAtUtc);
        Assert.Equal(replacement.Id, session.ReplacedBySessionId);
        Assert.True(session.IsRotated);
        Assert.False(session.IsActive(later));

        Assert.NotEqual(session.Id, replacement.Id);
        Assert.Equal(session.FamilyId, replacement.FamilyId);
        Assert.Equal(UserId, replacement.UserId);
        Assert.Equal(NextHash, replacement.RefreshTokenHash);
        Assert.Equal(later + Lifetime, replacement.ExpiresAtUtc);
        Assert.True(replacement.IsActive(later));
    }

    [Fact]
    public void RotateTo_OnInactiveSession_Throws()
    {
        var session = UserSession.Start(UserId, Hash, Now, Lifetime);

        Assert.Throws<InvalidOperationException>(() => session.RotateTo(NextHash, Now + Lifetime, Lifetime));
    }
}
