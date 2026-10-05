using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Users;
using LifeOS.Domain.Users;

namespace LifeOS.IntegrationTests.Http;

// AUTO-001 WP1: PUT /api/me/time-zone.
public class TimeZoneHttpTests
{
    private const string Path = "/api/me/time-zone";

    [Fact]
    public async Task ValidIanaZone_Returns204_AndStoresIt()
    {
        await using var factory = new LifeOSApiFactory();
        var (client, userId) = SignedIn(factory);

        var response = await client.PutAsJsonAsync(Path, new SetTimeZoneRequest("Europe/Rome"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("Europe/Rome", factory.Users.Stored(userId).TimeZoneId);
    }

    [Fact]
    public async Task SameZoneTwice_IsIdempotent()
    {
        await using var factory = new LifeOSApiFactory();
        var (client, userId) = SignedIn(factory);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Path, new SetTimeZoneRequest("Europe/Rome"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Path, new SetTimeZoneRequest("Europe/Rome"))).StatusCode);
        Assert.Equal("Europe/Rome", factory.Users.Stored(userId).TimeZoneId);
    }

    [Theory]
    [InlineData("W. Europe Standard Time")]
    [InlineData("+02:00")]
    [InlineData("Definitely/NotAZone")]
    [InlineData("")]
    public async Task InvalidZone_Returns400ValidationProblem_AndKeepsStoredValue(string timeZoneId)
    {
        await using var factory = new LifeOSApiFactory();
        var (client, userId) = SignedIn(factory);
        await client.PutAsJsonAsync(Path, new SetTimeZoneRequest("Europe/Rome"));

        var response = await client.PutAsJsonAsync(Path, new SetTimeZoneRequest(timeZoneId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("timeZoneId", await response.Content.ReadAsStringAsync());
        Assert.Equal("Europe/Rome", factory.Users.Stored(userId).TimeZoneId);
    }

    [Fact]
    public async Task WithoutToken_Returns401()
    {
        await using var factory = new LifeOSApiFactory();

        var response = await factory.CreateClient().PutAsJsonAsync(Path, new SetTimeZoneRequest("Europe/Rome"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingUser_Returns404()
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.IssueAccessToken(Guid.CreateVersion7()));

        var response = await client.PutAsJsonAsync(Path, new SetTimeZoneRequest("Europe/Rome"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static (HttpClient Client, Guid UserId) SignedIn(LifeOSApiFactory factory)
    {
        var client = factory.CreateClient();
        var user = User.CreateFromExternalIdentity(null, null, new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero));
        factory.Users.Users.Add(user);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.IssueAccessToken(user.Id));

        return (client, user.Id);
    }
}
