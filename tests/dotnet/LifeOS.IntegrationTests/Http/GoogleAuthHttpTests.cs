using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Api.Authentication;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using AppPkce = LifeOS.App.Services.Auth.Pkce;

namespace LifeOS.IntegrationTests.Http;

// The permanent Google sign-in flow without Google's website: /start is checked up to the redirect
// to Google, and the completion runs through ExternalSignInCompletion with a verified identity in
// provider-neutral form. The real Google round trip is a manual device test.
public class GoogleAuthHttpTests
{
    private const string StartPath = "/api/auth/google/start";
    private const string TokenPath = "/api/auth/token";

    // ---- /api/auth/google/start ----

    [Fact]
    public async Task Start_WithValidRequest_RedirectsToGoogleWithoutOurChallenge()
    {
        await using var factory = new LifeOSApiFactory();
        var challenge = AppPkce.CreateS256Challenge(AppPkce.CreateVerifier());

        var response = await NoRedirectClient(factory).GetAsync(StartUrl(challenge, AppCallbacks.Auth));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("accounts.google.com", location.Host);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("http://localhost/signin-google", query["redirect_uri"].ToString());
        // Our PKCE challenge travels only inside the protected state, never as a readable parameter.
        Assert.DoesNotContain(challenge, location.ToString());
    }

    [Theory]
    [InlineData("lifeos://other")]
    [InlineData("lifeos://auth/")]
    [InlineData("https://evil.example/callback")]
    [InlineData("")]
    public async Task Start_WithRedirectUriNotAllowlisted_Returns400(string redirectUri)
    {
        await using var factory = new LifeOSApiFactory();
        var challenge = AppPkce.CreateS256Challenge(AppPkce.CreateVerifier());

        var response = await NoRedirectClient(factory).GetAsync(StartUrl(challenge, redirectUri));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-challenge")]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cN")] // non-canonical
    public async Task Start_WithMissingOrMalformedChallenge_Returns400(string? challenge)
    {
        await using var factory = new LifeOSApiFactory();

        var response = await NoRedirectClient(factory).GetAsync(StartUrl(challenge, AppCallbacks.Auth));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Start_WithPlainChallengeMethod_Returns400()
    {
        await using var factory = new LifeOSApiFactory();
        var challenge = AppPkce.CreateS256Challenge(AppPkce.CreateVerifier());

        var response = await NoRedirectClient(factory).GetAsync(StartUrl(challenge, AppCallbacks.Auth) + "&code_challenge_method=plain");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Complete_WithoutAGoogleResult_RedirectsToTheAppWithAnErrorOnly()
    {
        await using var factory = new LifeOSApiFactory();

        var response = await NoRedirectClient(factory).GetAsync("/api/auth/google/complete?code_challenge=x&redirect_uri=https://evil.example");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("lifeos://auth?error=sign_in_failed", response.Headers.Location!.OriginalString);
    }

    // ---- completion (provider-neutral) ----

    [Fact]
    public async Task Completion_RedirectsWithOnlyAOneTimeCode()
    {
        await using var factory = new LifeOSApiFactory();
        var verifier = AppPkce.CreateVerifier();

        var callback = await CompleteAsync(factory, "google-subject-1", verifier);

        Assert.StartsWith("lifeos://auth?code=", callback);
        var query = QueryHelpers.ParseQuery(new Uri(callback).Query);
        Assert.Equal(["code"], query.Keys);
        Assert.DoesNotContain("google-subject-1", callback);
        Assert.DoesNotContain("person@example.com", callback);
    }

    [Fact]
    public async Task Completion_UsesProviderGoogleAndResolvesTheSameUserEachTime()
    {
        await using var factory = new LifeOSApiFactory();

        await CompleteAsync(factory, "google-subject-1", AppPkce.CreateVerifier());
        await CompleteAsync(factory, "google-subject-1", AppPkce.CreateVerifier());

        var identity = Assert.Single(factory.Users.Identities);
        Assert.Equal("google", identity.Provider);
        Assert.Single(factory.Users.Users);
    }

    // ---- /api/auth/token ----

    [Fact]
    public async Task Token_WithCorrectVerifier_ReturnsLifeOSTokensForTheSignedInUser()
    {
        await using var factory = new LifeOSApiFactory();
        var verifier = AppPkce.CreateVerifier();
        var code = CodeOf(await CompleteAsync(factory, "google-subject-1", verifier));

        var response = await factory.CreateClient().PostAsJsonAsync(TokenPath, new TokenExchangeRequest(code, verifier));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var tokens = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Single(factory.Sessions.Sessions);

        var me = await GetMeAsync(factory, tokens.AccessToken);
        Assert.Equal(Assert.Single(factory.Users.Users).Id, me.UserId);
    }

    [Fact]
    public async Task Token_CodeIsSingleUse()
    {
        await using var factory = new LifeOSApiFactory();
        var verifier = AppPkce.CreateVerifier();
        var code = CodeOf(await CompleteAsync(factory, "google-subject-1", verifier));
        var client = factory.CreateClient();

        var first = await client.PostAsJsonAsync(TokenPath, new TokenExchangeRequest(code, verifier));
        var second = await client.PostAsJsonAsync(TokenPath, new TokenExchangeRequest(code, verifier));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        Assert.Single(factory.Sessions.Sessions);
    }

    [Fact]
    public async Task Token_ConcurrentExchangesOfOneCode_OnlyOneSucceeds()
    {
        await using var factory = new LifeOSApiFactory();
        var verifier = AppPkce.CreateVerifier();
        var code = CodeOf(await CompleteAsync(factory, "google-subject-1", verifier));
        var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => client.PostAsJsonAsync(TokenPath, new TokenExchangeRequest(code, verifier))));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(factory.Sessions.Sessions);
    }

    [Fact]
    public async Task Token_WithWrongVerifier_IsRejectedAndBurnsTheCode()
    {
        await using var factory = new LifeOSApiFactory();
        var verifier = AppPkce.CreateVerifier();
        var code = CodeOf(await CompleteAsync(factory, "google-subject-1", verifier));
        var client = factory.CreateClient();

        var wrong = await client.PostAsJsonAsync(TokenPath, new TokenExchangeRequest(code, AppPkce.CreateVerifier()));
        var thenRight = await client.PostAsJsonAsync(TokenPath, new TokenExchangeRequest(code, verifier));

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, thenRight.StatusCode);
        Assert.Empty(factory.Sessions.Sessions);
    }

    [Fact]
    public async Task Token_AfterCodeLifetime_IsRejected()
    {
        await using var factory = new LifeOSApiFactory();
        var verifier = AppPkce.CreateVerifier();
        var code = CodeOf(await CompleteAsync(factory, "google-subject-1", verifier));

        factory.Clock.Advance(AuthorizationCodeStore.Lifetime + TimeSpan.FromSeconds(1));
        var response = await factory.CreateClient().PostAsJsonAsync(TokenPath, new TokenExchangeRequest(code, verifier));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Sessions.Sessions);
    }

    [Fact]
    public async Task Token_WithUnknownCode_IsRejected()
    {
        await using var factory = new LifeOSApiFactory();

        var response = await factory.CreateClient().PostAsJsonAsync(
            TokenPath, new TokenExchangeRequest("unknown-code", AppPkce.CreateVerifier()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(null, "valid")]
    [InlineData("", "valid")]
    [InlineData("some-code", null)]
    [InlineData("some-code", "too-short")]
    [InlineData("some-code", "has spaces in it which are not allowed by rfc 7636 at all")]
    public async Task Token_WithMalformedRequest_Returns400(string? code, string? verifier)
    {
        await using var factory = new LifeOSApiFactory();

        var response = await factory.CreateClient().PostAsJsonAsync(
            TokenPath, new TokenExchangeRequest(code, verifier == "valid" ? AppPkce.CreateVerifier() : verifier));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- helpers ----

    private static HttpClient NoRedirectClient(LifeOSApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string StartUrl(string? challenge, string? redirectUri)
    {
        var parameters = new Dictionary<string, string?>();

        if (challenge is not null)
        {
            parameters["code_challenge"] = challenge;
        }

        if (redirectUri is not null)
        {
            parameters["redirect_uri"] = redirectUri;
        }

        return QueryHelpers.AddQueryString(StartPath, parameters);
    }

    // Simulates /google/complete after Google verified the user.
    private static async Task<string> CompleteAsync(LifeOSApiFactory factory, string subject, string verifier)
    {
        await using var scope = factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<ExternalSignInCompletion>().CompleteAsync(
            new VerifiedExternalIdentity("google", subject, "person@example.com", "Person"),
            AppPkce.CreateS256Challenge(verifier),
            AppCallbacks.Auth,
            CancellationToken.None);
    }

    private static string CodeOf(string callback) =>
        QueryHelpers.ParseQuery(new Uri(callback).Query)["code"].ToString();

    private static async Task<MeResponse> GetMeAsync(LifeOSApiFactory factory, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await factory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<MeResponse>())!;
    }
}
