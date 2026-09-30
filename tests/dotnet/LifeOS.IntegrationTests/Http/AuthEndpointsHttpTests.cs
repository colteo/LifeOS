using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Api.Authentication;
using LifeOS.Application.Authentication;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Users;
using LifeOS.UnitTests.Fakes;
using Microsoft.Extensions.Configuration;

namespace LifeOS.IntegrationTests.Http;

public class AuthEndpointsHttpTests
{
    private const string SignInPath = "/api/auth/dev/sign-in";

    // ---- Development sign-in guard ----
    // The fallback authorization policy answers 401 to anonymous callers for every path, mapped or
    // not. "Not mapped" is therefore proven with a valid access token: only a missing route is 404.

    [Fact]
    public async Task DevSignIn_DevelopmentWithFlagOff_IsNotMapped()
    {
        await using var factory = new LifeOSApiFactory("Development", developmentSignInEnabled: false);

        var response = await PostSignInAuthenticatedAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DevSignIn_WhenNotMapped_IsUnauthorizedForAnonymousCallers()
    {
        await using var factory = new LifeOSApiFactory("Development", developmentSignInEnabled: false);

        var response = await factory.CreateClient().PostAsJsonAsync(SignInPath, new DevSignInRequest("dev-user", null, null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DevSignIn_DevelopmentWithFlagOn_ReturnsTokens()
    {
        await using var factory = new LifeOSApiFactory("Development", developmentSignInEnabled: true);

        var response = await factory.CreateClient().PostAsJsonAsync(
            SignInPath,
            new DevSignInRequest("dev-user", "dev@example.com", "Dev User"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);

        var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(tokens);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        Assert.Equal(900, tokens.ExpiresIn);

        // Provider is fixed to "dev"; only the refresh token's hash is stored.
        var identity = Assert.Single(factory.Users.Identities);
        Assert.Equal(DevelopmentSignIn.Provider, identity.Provider);
        Assert.Equal("dev-user", identity.Subject);
        var session = Assert.Single(factory.Sessions.Sessions);
        Assert.Equal(RefreshTokens.Hash(tokens.RefreshToken), session.RefreshTokenHash);
    }

    [Fact]
    public async Task DevSignIn_NonDevelopmentWithFlagOff_IsNotMapped()
    {
        await using var factory = new LifeOSApiFactory("Production", developmentSignInEnabled: false);

        var response = await PostSignInAuthenticatedAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Startup_NonDevelopmentWithFlagOn_Fails()
    {
        await using var factory = new LifeOSApiFactory("Production", developmentSignInEnabled: true);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(DevelopmentSignIn.EnabledKey, FullMessage(exception));
    }

    [Fact]
    public async Task DevSignIn_SameSubjectTwice_ResolvesSameUser()
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();

        var first = await SignInAsync(client, "dev-user");
        var second = await SignInAsync(client, "dev-user");

        Assert.Equal(await GetMeUserIdAsync(client, first.AccessToken), await GetMeUserIdAsync(client, second.AccessToken));
        Assert.Single(factory.Users.Users);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DevSignIn_WithBlankSubject_ReturnsValidationProblem(string? subject)
    {
        await using var factory = new LifeOSApiFactory();

        var response = await factory.CreateClient().PostAsJsonAsync(SignInPath, new DevSignInRequest(subject, null, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Users.Users);
    }

    // ---- Signing key validation ----

    [Theory]
    [InlineData("not base64!")]
    [InlineData("c2hvcnQta2V5")] // "short-key": fewer than 32 decoded bytes
    public async Task Startup_WithInvalidSigningKey_Fails(string signingKey)
    {
        await using var factory = new LifeOSApiFactory(signingKey: signingKey);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("SigningKey", FullMessage(exception));
    }

    // ---- GET /api/me ----

    [Fact]
    public async Task Me_WithoutToken_Returns401()
    {
        await using var factory = new LifeOSApiFactory();

        var response = await factory.CreateClient().GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithValidToken_ReturnsProfile()
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();
        var tokens = await SignInAsync(client, "dev-user", "dev@example.com", "Dev User");

        var response = await SendMeAsync(client, tokens.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<MeResponse>();
        Assert.NotNull(me);
        Assert.Equal(Assert.Single(factory.Users.Users).Id, me.UserId);
        Assert.Equal("Dev User", me.DisplayName);
        Assert.Equal("dev@example.com", me.Email);
        Assert.Equal("PendingFinanceProfile", me.OnboardingStatus);
        Assert.Null(me.DefaultCurrency);
    }

    [Fact]
    public async Task Me_WithExpiredToken_Returns401()
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();
        var userId = await GetMeUserIdAsync(client, (await SignInAsync(client, "dev-user")).AccessToken);

        // Issued two hours ago with the real key: expired well beyond the clock skew.
        var expired = new AccessTokenIssuer(factory.TokenOptions, new FixedTimeProvider(DateTimeOffset.UtcNow.AddHours(-2)))
            .Issue(userId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendMeAsync(client, expired)).StatusCode);
    }

    [Theory]
    [InlineData("signing-key")]
    [InlineData("issuer")]
    [InlineData("audience")]
    public async Task Me_WithTokenFromWrongSigner_Returns401(string mismatch)
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();
        var userId = await GetMeUserIdAsync(client, (await SignInAsync(client, "dev-user")).AccessToken);
        var real = factory.TokenOptions;

        var forged = LifeOSTokenOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:LifeOS:Issuer"] = mismatch == "issuer" ? "someone-else" : real.Issuer,
                ["Authentication:LifeOS:Audience"] = mismatch == "audience" ? "someone-else" : real.Audience,
                ["Authentication:LifeOS:SigningKey"] = mismatch == "signing-key"
                    ? LifeOSApiFactory.NewSigningKey()
                    : Convert.ToBase64String(real.SigningKey),
                ["Authentication:LifeOS:AccessTokenLifetime"] = "00:15:00",
                ["Authentication:LifeOS:RefreshTokenLifetime"] = "30.00:00:00"
            })
            .Build());

        var token = new AccessTokenIssuer(forged, TimeProvider.System).Issue(userId);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendMeAsync(client, token)).StatusCode);
    }

    [Fact]
    public async Task Me_WithMalformedToken_Returns401()
    {
        await using var factory = new LifeOSApiFactory();

        var response = await SendMeAsync(factory.CreateClient(), "not-a-jwt");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithRefreshTokenInsteadOfAccessToken_Returns401()
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();
        var tokens = await SignInAsync(client, "dev-user");

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendMeAsync(client, tokens.RefreshToken)).StatusCode);
    }

    // ---- Refresh and logout ----

    [Fact]
    public async Task Refresh_ReturnsNewTokensAndRejectsTheOldRefreshToken()
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();
        var original = await SignInAsync(client, "dev-user");

        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(original.RefreshToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var refreshed = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(refreshed);
        Assert.NotEqual(original.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, (await SendMeAsync(client, refreshed.AccessToken)).StatusCode);

        // Reuse of the rotated token is rejected and ends the whole session family.
        var reuse = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(original.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        var afterReuse = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(refreshed.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown-token")]
    public async Task Refresh_WithMissingOrUnknownToken_Returns401(string? refreshToken)
    {
        await using var factory = new LifeOSApiFactory();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(refreshToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesSession()
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();
        var tokens = await SignInAsync(client, "dev-user");

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(tokens.RefreshToken));

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.All(factory.Sessions.Sessions, session => Assert.NotNull(session.RevokedAtUtc));
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshTokenRequest(tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown-token")]
    public async Task Logout_WithMissingOrUnknownToken_Returns204(string? refreshToken)
    {
        await using var factory = new LifeOSApiFactory();

        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/logout", new LogoutRequest(refreshToken));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // ---- helpers ----

    private static Task<HttpResponseMessage> PostSignInAuthenticatedAsync(LifeOSApiFactory factory)
    {
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, SignInPath)
        {
            Content = JsonContent.Create(new DevSignInRequest("dev-user", null, null))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.IssueAccessToken(Guid.CreateVersion7()));

        return client.SendAsync(request);
    }

    private static async Task<TokenResponse> SignInAsync(
        HttpClient client,
        string subject,
        string? email = null,
        string? displayName = null)
    {
        var response = await client.PostAsJsonAsync(SignInPath, new DevSignInRequest(subject, email, displayName));
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private static Task<HttpResponseMessage> SendMeAsync(HttpClient client, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return client.SendAsync(request);
    }

    private static async Task<Guid> GetMeUserIdAsync(HttpClient client, string accessToken)
    {
        var response = await SendMeAsync(client, accessToken);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<MeResponse>())!.UserId;
    }

    private static string FullMessage(Exception exception)
    {
        var messages = new List<string>();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }
}
