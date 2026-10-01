using System.Net;
using System.Net.Http.Json;
using LifeOS.Api.Authentication;
using LifeOS.Contracts.Auth;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using AppPkce = LifeOS.App.Services.Auth.Pkce;

namespace LifeOS.IntegrationTests.Http;

// The whole Google sign-in round trip through the real pipeline, as Render's edge proxy delivers it: /start,
// Google's redirect back to /signin-google (handled by the ASP.NET Google handler), /complete and the
// code exchange. Only Google's back channel (token and userinfo endpoints) is faked; the browser's
// cookies are carried by hand because the forwarded scheme makes them Secure.
public class GoogleSignInHostingHttpTests
{
    private const string PublicHost = "lifeos-api.test.invalid";
    private const string HttpsSignInRedirect = "https://" + PublicHost + "/signin-google";

    // ---- forwarded headers / HTTPS ----

    [Fact]
    public async Task Production_ForwardedHttps_BuildsAnHttpsGoogleRedirectUri()
    {
        var google = new FakeGoogleBackchannel();
        await using var factory = ProductionFactory(google);

        var flow = await SignInThroughGoogleAsync(factory, AppCallbacks.Production, forwardedProto: "https");

        Assert.Equal(HttpsSignInRedirect, flow.GoogleRedirectUri);
        // The callback handler (which runs in the authentication middleware) saw HTTPS as well:
        // it sends Google the same redirect_uri when redeeming the code.
        Assert.Equal(HttpsSignInRedirect, Assert.Single(google.TokenRequestRedirectUris));
        Assert.Contains(flow.StartCookies, cookie => cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(flow.SignInCookies, cookie =>
            cookie.StartsWith("LifeOS.External=", StringComparison.Ordinal)
            && cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Development_IgnoresForwardedHeaders()
    {
        var google = new FakeGoogleBackchannel();
        await using var factory = new LifeOSApiFactory(configure: UseFakeGoogle(google));

        var flow = await SignInThroughGoogleAsync(factory, AppCallbacks.Development, forwardedProto: "https");

        Assert.Equal("http://" + PublicHost + "/signin-google", flow.GoogleRedirectUri);
    }

    [Fact]
    public async Task Production_ForwardedHttps_IsNotRedirectedToHttps()
    {
        // With an HTTPS port configured, a request the API believes is plain HTTP is redirected.
        await using var factory = new LifeOSApiFactory(
            "Production",
            developmentSignInEnabled: false,
            configure: builder => builder.UseSetting("https_port", "443"));
        var client = BrowserClient(factory);
        var start = StartUrl(AppCallbacks.Production);

        var plain = await client.GetAsync(start);
        var forwarded = await client.SendAsync(Get(start, forwardedProto: "https"));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, plain.StatusCode);
        Assert.Equal("https", plain.Headers.Location!.Scheme);
        Assert.Equal(HttpStatusCode.Redirect, forwarded.StatusCode);
        Assert.Equal("accounts.google.com", forwarded.Headers.Location!.Host);
    }

    // ---- callback per environment ----

    [Fact]
    public async Task Production_CompletesToTheProductionAppOnly_AndTheCodeSignsIn()
    {
        var google = new FakeGoogleBackchannel();
        await using var factory = ProductionFactory(google);

        var flow = await SignInThroughGoogleAsync(factory, AppCallbacks.Production, forwardedProto: "https");

        Assert.StartsWith("lifeos://auth?code=", flow.AppCallback);
        Assert.DoesNotContain("lifeos-dev", flow.AppCallback);

        var code = QueryHelpers.ParseQuery(new Uri(flow.AppCallback).Query)["code"].ToString();
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/token", new TokenExchangeRequest(code, flow.Verifier));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(factory.Users.Users);
        Assert.Equal("google", Assert.Single(factory.Users.Identities).Provider);
        Assert.Single(factory.Sessions.Sessions);
    }

    [Fact]
    public async Task Development_CompletesToTheDevelopmentApp()
    {
        var google = new FakeGoogleBackchannel();
        await using var factory = new LifeOSApiFactory(configure: UseFakeGoogle(google));

        var flow = await SignInThroughGoogleAsync(factory, AppCallbacks.Development, forwardedProto: null);

        Assert.StartsWith("lifeos-dev://auth?code=", flow.AppCallback);
    }

    [Theory]
    [InlineData("lifeos-dev://auth")]
    [InlineData("lifeos://auth/")]
    [InlineData("https://evil.example/callback")]
    public async Task Production_Start_RejectsAnyOtherCallback(string redirectUri)
    {
        await using var factory = ProductionFactory(new FakeGoogleBackchannel());

        var response = await BrowserClient(factory).SendAsync(Get(StartUrl(redirectUri), forwardedProto: "https"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Production_CompleteWithoutAGoogleResult_ReturnsAnErrorToTheProductionApp()
    {
        await using var factory = ProductionFactory(new FakeGoogleBackchannel());

        var response = await BrowserClient(factory).SendAsync(
            Get("/api/auth/google/complete?redirect_uri=lifeos-dev://auth", forwardedProto: "https"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("lifeos://auth?error=sign_in_failed", response.Headers.Location!.OriginalString);
    }

    // ---- allowlist ----

    [Fact]
    public async Task Production_AllowlistMatchesTrimmedAndCaseInsensitively()
    {
        var google = new FakeGoogleBackchannel { Email = "Person@Example.com" };
        await using var factory = ProductionFactory(google, allowedEmails: ["  PERSON@example.COM  "]);

        var flow = await SignInThroughGoogleAsync(factory, AppCallbacks.Production, forwardedProto: "https");

        Assert.StartsWith("lifeos://auth?code=", flow.AppCallback);
        Assert.Single(factory.Users.Users);
    }

    [Theory]
    [InlineData("someone.else@example.com", true)]
    [InlineData(LifeOSApiFactory.AllowedEmail, false)] // allowlisted address, but not verified by Google
    public async Task Production_AccountNotAllowed_CreatesNothing(string email, bool emailVerified)
    {
        var google = new FakeGoogleBackchannel { Email = email, EmailVerified = emailVerified };
        await using var factory = ProductionFactory(google);

        var flow = await SignInThroughGoogleAsync(factory, AppCallbacks.Production, forwardedProto: "https");

        Assert.Equal("lifeos://auth?error=sign_in_failed", flow.AppCallback);
        Assert.Empty(factory.Users.Users);
        Assert.Empty(factory.Users.Identities);
        Assert.Empty(factory.Sessions.Sessions);
    }

    [Fact]
    public async Task Production_AccountNotAllowed_IsNotAllowedIntoAnExistingAccountEither()
    {
        var google = new FakeGoogleBackchannel();
        await using var factory = ProductionFactory(google);
        await SignInThroughGoogleAsync(factory, AppCallbacks.Production, forwardedProto: "https");

        // The same Google subject later reports an address that is no longer allowlisted.
        google.Email = "someone.else@example.com";
        var flow = await SignInThroughGoogleAsync(factory, AppCallbacks.Production, forwardedProto: "https");

        Assert.Equal("lifeos://auth?error=sign_in_failed", flow.AppCallback);
        Assert.Single(factory.Users.Users);
    }

    // ---- startup validation ----

    [Fact]
    public async Task Startup_ProductionWithoutAllowlist_Fails()
    {
        await using var factory = new LifeOSApiFactory("Production", developmentSignInEnabled: false, allowedEmails: []);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(GoogleAccountAllowlist.AllowedEmailsKey, FullMessage(exception));
    }

    [Fact]
    public async Task Startup_ProductionWithoutGoogleCredentials_Fails()
    {
        await using var factory = new LifeOSApiFactory(
            "Production",
            developmentSignInEnabled: false,
            configure: builder => builder
                .UseSetting("Authentication:Google:ClientId", "")
                .UseSetting("Authentication:Google:ClientSecret", ""));

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var message = FullMessage(exception);

        Assert.Contains("Authentication:Google:ClientId", message);
        Assert.DoesNotContain("test-client-secret", message);
    }

    [Fact]
    public async Task Startup_DevelopmentWithoutAllowlist_StillStarts()
    {
        await using var factory = new LifeOSApiFactory(allowedEmails: []);

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- helpers ----

    private sealed record GoogleFlow(
        string GoogleRedirectUri,
        IReadOnlyList<string> StartCookies,
        IReadOnlyList<string> SignInCookies,
        string AppCallback,
        string Verifier);

    private static LifeOSApiFactory ProductionFactory(FakeGoogleBackchannel google, string[]? allowedEmails = null) =>
        new("Production", developmentSignInEnabled: false, allowedEmails: allowedEmails, configure: UseFakeGoogle(google));

    private static Action<IWebHostBuilder> UseFakeGoogle(FakeGoogleBackchannel google) =>
        builder => builder.ConfigureTestServices(services =>
            services.Configure<GoogleOptions>(GoogleDefaults.AuthenticationScheme, options => options.BackchannelHttpHandler = google));

    // The browser: no automatic redirects or cookies, on the public host the proxy forwards.
    private static HttpClient BrowserClient(LifeOSApiFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("http://" + PublicHost)
        });

    private static async Task<GoogleFlow> SignInThroughGoogleAsync(LifeOSApiFactory factory, string appCallback, string? forwardedProto)
    {
        var client = BrowserClient(factory);
        var verifier = AppPkce.CreateVerifier();

        // 1. The app opens /start in the browser; the API redirects to Google.
        var start = await client.SendAsync(Get(StartUrl(appCallback, AppPkce.CreateS256Challenge(verifier)), forwardedProto));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var googleQuery = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        var startCookies = SetCookies(start);

        // 2. Google sends the browser back to /signin-google with its code and our state.
        var signIn = Get($"/signin-google?code=fake-google-code&state={Uri.EscapeDataString(googleQuery["state"].ToString())}", forwardedProto);
        signIn.Headers.Add("Cookie", CookieHeader(startCookies));
        var signInResponse = await client.SendAsync(signIn);
        Assert.Equal(HttpStatusCode.Redirect, signInResponse.StatusCode);
        var signInCookies = SetCookies(signInResponse);

        // 3. The handler continues to /complete, which redirects to the app.
        var complete = Get(signInResponse.Headers.Location!.OriginalString, forwardedProto);
        complete.Headers.Add("Cookie", CookieHeader(signInCookies));
        var completeResponse = await client.SendAsync(complete);
        Assert.Equal(HttpStatusCode.Redirect, completeResponse.StatusCode);

        return new GoogleFlow(
            googleQuery["redirect_uri"].ToString(),
            startCookies,
            signInCookies,
            completeResponse.Headers.Location!.OriginalString,
            verifier);
    }

    private static string StartUrl(string redirectUri, string? challenge = null) =>
        QueryHelpers.AddQueryString("/api/auth/google/start", new Dictionary<string, string?>
        {
            ["code_challenge"] = challenge ?? AppPkce.CreateS256Challenge(AppPkce.CreateVerifier()),
            ["redirect_uri"] = redirectUri
        });

    // A request as Render's edge proxy delivers it: plain HTTP plus the forwarded headers.
    private static HttpRequestMessage Get(string url, string? forwardedProto)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
            request.Headers.Add("X-Forwarded-For", "203.0.113.7");
        }

        return request;
    }

    private static List<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];

    private static string CookieHeader(IEnumerable<string> setCookies) =>
        string.Join("; ", setCookies.Select(cookie => cookie.Split(';')[0]));

    private static string FullMessage(Exception exception) =>
        exception.InnerException is null ? exception.Message : exception.Message + " " + FullMessage(exception.InnerException);
}

// Google's token and userinfo endpoints. Records the redirect_uri sent when redeeming the code.
internal sealed class FakeGoogleBackchannel : HttpMessageHandler
{
    public string Subject { get; set; } = "google-subject-1";

    public string Email { get; set; } = LifeOSApiFactory.AllowedEmail;

    public bool EmailVerified { get; set; } = true;

    public List<string> TokenRequestRedirectUris { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);

        if (url == GoogleDefaults.TokenEndpoint)
        {
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            TokenRequestRedirectUris.Add(form["redirect_uri"].ToString());

            return Json(new { access_token = "fake-google-access-token", token_type = "Bearer", expires_in = 3600 });
        }

        if (url == GoogleDefaults.UserInformationEndpoint)
        {
            return Json(new { sub = Subject, email = Email, email_verified = EmailVerified, name = "Person" });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
}
