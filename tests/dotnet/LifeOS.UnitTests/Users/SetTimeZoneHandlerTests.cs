using LifeOS.Application.Users.SetTimeZone;
using LifeOS.Domain.Users;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Users;

public class SetTimeZoneHandlerTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WithValidIanaZone_StoresZone()
    {
        var repository = new InMemoryUserRepository();
        var user = User.CreateFromExternalIdentity(null, null, CreatedAtUtc);
        repository.Users.Add(user);

        var result = await new SetTimeZoneHandler(repository)
            .HandleAsync(user.Id, " Europe/Rome ", CancellationToken.None);

        Assert.Equal(SetTimeZoneResult.Updated, result);
        Assert.Equal("Europe/Rome", repository.Stored(user.Id).TimeZoneId);
    }

    [Fact]
    public async Task HandleAsync_WithSameZone_IsIdempotent()
    {
        var repository = new InMemoryUserRepository();
        var user = User.CreateFromExternalIdentity(null, null, CreatedAtUtc);
        user.SetTimeZone("Europe/Rome");
        repository.Users.Add(user);

        var result = await new SetTimeZoneHandler(repository)
            .HandleAsync(user.Id, "Europe/Rome", CancellationToken.None);

        Assert.Equal(SetTimeZoneResult.Unchanged, result);
        Assert.Equal("Europe/Rome", repository.Stored(user.Id).TimeZoneId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+02:00")]
    [InlineData("W. Europe Standard Time")]
    [InlineData("Definitely/NotAZone")]
    public async Task HandleAsync_WithInvalidZone_RejectsWithoutChangingUser(string? timeZoneId)
    {
        var repository = new InMemoryUserRepository();
        var user = User.CreateFromExternalIdentity(null, null, CreatedAtUtc);
        repository.Users.Add(user);

        var result = await new SetTimeZoneHandler(repository)
            .HandleAsync(user.Id, timeZoneId, CancellationToken.None);

        Assert.Equal(SetTimeZoneResult.Invalid, result);
        Assert.Null(repository.Stored(user.Id).TimeZoneId);
    }

    [Fact]
    public async Task HandleAsync_WhenUserMissing_ReturnsNotFound()
    {
        var repository = new InMemoryUserRepository();

        var result = await new SetTimeZoneHandler(repository)
            .HandleAsync(Guid.CreateVersion7(), "Europe/Rome", CancellationToken.None);

        Assert.Equal(SetTimeZoneResult.NotFound, result);
    }
}
