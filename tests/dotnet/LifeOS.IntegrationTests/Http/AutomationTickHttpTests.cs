using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LifeOS.Api.Automation;
using LifeOS.Application.Automation;
using LifeOS.Domain.Users;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.Http;

// AUTO-001 WP2: POST /api/internal/automation/tick behind the X-LifeOS-Automation-Key scheme.
public class AutomationTickHttpTests
{
    private const string Path = AutomationTickEndpoints.TickPath;
    private const string Header = AutomationKeyAuthenticationHandler.HeaderName;

    private static readonly string Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    // ---- Configuration ----

    // The fallback policy answers 401 to anonymous callers on any path, so "not mapped" is proven
    // with a valid user access token: only a missing route is 404.
    [Fact]
    public async Task KeyAbsent_EndpointIsNotMapped()
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(Path, null)).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", UserToken(factory));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(Path, null)).StatusCode);

        // And no handler, tick or key scheme is registered.
        Assert.Null(factory.Services.GetService<RunAutomationTick>());
    }

    [Theory]
    [InlineData("too-short")]
    [InlineData("0123456789abcdefghij klmnopqrstuvwxyz")]
    public async Task MalformedKey_FailsStartup(string key)
    {
        await using var factory = Enabled(key);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(AutomationConfiguration.TickKeyKey, FullMessage(exception));
        Assert.DoesNotContain(key, FullMessage(exception));
    }

    // ---- Authentication ----

    [Fact]
    public async Task NoKey_Returns401_WithEmptyBody()
    {
        await using var factory = Enabled();

        var response = await factory.CreateClient().PostAsync(Path, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.AutomationExecutions.Rows);
    }

    [Theory]
    [InlineData("wrong-key-of-sufficient-length-0123456789")]
    [InlineData("")]
    public async Task WrongKey_Returns401(string presented)
    {
        await using var factory = Enabled();

        var response = await SendAsync(factory, request => request.Headers.TryAddWithoutValidation(Header, presented));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task KeyWithDifferentCase_Returns401()
    {
        await using var factory = Enabled("abcdefghijklmnopqrstuvwxyz0123456789");

        var response = await SendAsync(factory, request => request.Headers.Add(Header, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The tick scheme is separate from the user pipeline: neither a user access token nor the tick
    // key sent as a bearer token is accepted.
    [Fact]
    public async Task BearerTokens_DoNotSubstituteForTheKey()
    {
        await using var factory = Enabled();

        var withUserToken = await SendAsync(factory, request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", UserToken(factory)));
        var withKeyAsBearer = await SendAsync(factory, request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key));

        Assert.Equal(HttpStatusCode.Unauthorized, withUserToken.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withKeyAsBearer.StatusCode);
    }

    // ---- Valid tick ----

    [Fact]
    public async Task CorrectKey_Returns200_WithCountsOnly_AndNoUserTokenNeeded()
    {
        await using var factory = Enabled();

        var response = await SendAsync(factory, WithKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["executions", "more"], body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(0, body.RootElement.GetProperty("executions").GetInt32());
        Assert.False(body.RootElement.GetProperty("more").GetBoolean());
    }

    [Fact]
    public async Task ZeroProductionHandlers_AreRegistered()
    {
        await using var factory = Enabled();
        factory.CreateClient();

        Assert.Empty(factory.Services.GetServices<IAutomationHandler>());

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RunAutomationTick>());
    }

    [Fact]
    public async Task TickWithinAMinute_IsSkipped()
    {
        await using var factory = Enabled();

        await SendAsync(factory, WithKey);
        var second = await SendAsync(factory, WithKey);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("""{"skipped":true}""", await second.Content.ReadAsStringAsync());

        factory.Clock.Advance(AutomationTickGuard.MinimumInterval);
        Assert.Contains("executions", await (await SendAsync(factory, WithKey)).Content.ReadAsStringAsync());
    }

    // ---- Parameterless ----

    [Fact]
    public async Task Body_Returns400()
    {
        await using var factory = Enabled();

        var response = await SendAsync(factory, request =>
        {
            WithKey(request);
            request.Content = new StringContent("""{"userId":"0192f0c3-0000-7000-8000-000000000001"}""", Encoding.UTF8, "application/json");
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Body_WithoutKey_Returns401()
    {
        await using var factory = Enabled();

        var response = await SendAsync(factory, request => request.Content = new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("?type=WeeklyReview")]
    [InlineData("?userId=0192f0c3-0000-7000-8000-000000000001")]
    [InlineData("?now")]
    public async Task Query_Returns400(string query)
    {
        await using var factory = Enabled();

        var response = await SendAsync(factory, WithKey, Path + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EmptyBodyWithContentLengthZero_IsAccepted()
    {
        await using var factory = Enabled();

        var response = await SendAsync(factory, request =>
        {
            WithKey(request);
            request.Content = new ByteArrayContent([]);
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // GET has no endpoint: the key is not a user, so the fallback policy answers 401; a signed-in
    // user gets 405. Neither runs a tick.
    [Fact]
    public async Task OnlyPost_IsMapped()
    {
        await using var factory = Enabled();
        var withKey = new HttpRequestMessage(HttpMethod.Get, Path);
        WithKey(withKey);
        var withUser = new HttpRequestMessage(HttpMethod.Get, Path);
        withUser.Headers.Authorization = new AuthenticationHeaderValue("Bearer", UserToken(factory));

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().SendAsync(withKey)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await factory.CreateClient().SendAsync(withUser)).StatusCode);
    }

    private static LifeOSApiFactory Enabled(string? key = null) =>
        new(configure: builder => builder.UseSetting(AutomationConfiguration.TickKeyKey, key ?? Key));

    private static void WithKey(HttpRequestMessage request) => request.Headers.Add(Header, Key);

    private static Task<HttpResponseMessage> SendAsync(LifeOSApiFactory factory, Action<HttpRequestMessage> configure, string path = Path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        configure(request);

        return factory.CreateClient().SendAsync(request);
    }

    private static string UserToken(LifeOSApiFactory factory)
    {
        var user = User.CreateFromExternalIdentity(null, null, new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero));
        factory.Users.Users.Add(user);

        return factory.IssueAccessToken(user.Id);
    }

    private static string FullMessage(Exception exception)
    {
        var messages = new List<string>();

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}
