using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LifeOS.App.Services.Auth;
using LifeOS.App.Services.Diagnostics;
using LifeOS.App.Services.Notifications;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Notifications;

namespace LifeOS.UnitTests.App;

// 1.4.1 Diagnostics: the version row under Sign out (the gear's Settings page), the /diagnostics
// page, and the push test transport (NotificationsApiClient). Razor is checked by source, as in
// UiConsolidationTests.
public class DiagnosticsAppTests
{
    // ---- Access ----

    [Fact]
    public void Settings_HasTheVersionRow_BelowSignOut_LinkingToDiagnostics()
    {
        var settings = Source("Pages", "Settings.razor");

        var signOut = settings.IndexOf("@onclick=\"Auth.SignOutAsync\">Sign out</button>", StringComparison.Ordinal);
        var version = settings.IndexOf(
            "<a href=\"diagnostics\" class=\"lo-version-link\">@DiagnosticsDisplay.VersionLabel(AppInfo.Current.VersionString)</a>",
            StringComparison.Ordinal);

        Assert.True(signOut >= 0 && version > signOut);
        Assert.Single(Regex.Matches(settings, "Auth.SignOutAsync"));
    }

    [Fact]
    public void VersionText_IsDynamic()
    {
        Assert.Equal("LifeOS 1.4.1", DiagnosticsDisplay.VersionLabel("1.4.1"));
        Assert.Equal("LifeOS 9.0.0", DiagnosticsDisplay.VersionLabel("9.0.0"));
        Assert.DoesNotMatch(new Regex(@"LifeOS \d"), Source("Pages", "Settings.razor"));
    }

    [Fact]
    public void DiagnosticsPage_IsRoutedAndTitled()
    {
        var page = Source("Pages", "Diagnostics.razor");

        Assert.StartsWith("@page \"/diagnostics\"", page);
        Assert.Contains("<PageHeader Title=\"Diagnostics\" BackHref=\"settings\" />", page);
        Assert.Contains("@AppInfo.Current.VersionString", page);
        Assert.Contains("@AppInfo.Current.BuildString", page);
        Assert.Contains("DiagnosticsDisplay.ApiHost(Api.BaseAddress)", page);
        Assert.Contains("Send test notification", page);
        foreach (var secret in new[] { "AccessToken", "RefreshToken", "Authorization", "Bearer", "GetTokenAsync" })
        {
            Assert.DoesNotContain(secret, page);
        }
    }

    [Fact]
    public void Diagnostics_IsNotInMoreDockHomeOrHeader()
    {
        var more = Source("Pages", "More.razor");

        Assert.Equal(3, Regex.Matches(more, @"new\(""").Count);
        Assert.Contains("new(\"Finance\", \"Transactions, accounts and categories\", \"finance\", \"finance\")", more);
        Assert.Contains("new(\"Gym\", \"Train and manage workout programs\", \"gym\", \"gym\")", more);
        Assert.Contains("new(\"Nutrition\", \"Food diary and targets\", \"nutrition\", \"nutrition/hub\")", more);
        foreach (var (folder, file) in new[] { ("Pages", "More.razor"), ("Pages", "Home.razor"), ("Layout", "BottomDock.razor"), ("Layout", "AppHeader.razor") })
        {
            Assert.DoesNotContain("diagnostics", Source(folder, file), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Release_IsVersion141()
    {
        var project = File.ReadAllText(Path.Combine(ComponentsRoot(), "..", "LifeOS.App.csproj"));

        Assert.Contains("<ApplicationDisplayVersion>1.4.1</ApplicationDisplayVersion>", project);
        Assert.Contains("<ApplicationVersion>14001</ApplicationVersion>", project);
    }

    // ---- Fields ----

    [Fact]
    public void Environment_IsProductionOnlyForRelease()
    {
        Assert.Equal("Production", DiagnosticsDisplay.Environment(true));
        Assert.Equal("Development", DiagnosticsDisplay.Environment(false));
    }

    [Theory]
    [InlineData("https://api.example.com/", "api.example.com")]
    [InlineData("https://api.example.com/base/path/", "api.example.com")]
    [InlineData("http://10.0.2.2:5050/", "10.0.2.2:5050")]
    public void ApiHost_IsTheHostOnly(string baseAddress, string expected) =>
        Assert.Equal(expected, DiagnosticsDisplay.ApiHost(new Uri(baseAddress)));

    // ---- Push test ----

    [Fact]
    public async Task SendTest_PostsWithoutBodyOrQuery()
    {
        HttpRequestMessage? sent = null;
        var client = new NotificationsApiClient(new HttpClient(new StubHandler(request =>
        {
            sent = request;
            return Task.FromResult(Ok(1, 1, 0));
        })) { BaseAddress = new("http://test/") });

        await client.SendTestAsync();

        Assert.NotNull(sent);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/api/notifications/test", sent.RequestUri!.AbsolutePath);
        Assert.Equal(string.Empty, sent.RequestUri.Query);
        Assert.Null(sent.Content);
    }

    [Fact]
    public async Task Success_ReportsTheCounts()
    {
        var result = await Client(Ok(2, 1, 1)).SendTestAsync();

        Assert.Equal(new TestNotificationResult(TestNotificationStatus.Sent, 2, 1, 1), result);
        Assert.Equal("Sent to 2 device(s). Sent: 1. Failed: 1.", DiagnosticsDisplay.TestNotificationMessage(result));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "No active notification device.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Push notifications are not configured.")]
    [InlineData(HttpStatusCode.TooManyRequests, "Try again later.")]
    [InlineData(HttpStatusCode.InternalServerError, DiagnosticsDisplay.GenericError)]
    [InlineData(HttpStatusCode.Unauthorized, DiagnosticsDisplay.GenericError)]
    [InlineData(HttpStatusCode.BadRequest, DiagnosticsDisplay.GenericError)]
    public async Task Failures_MapToFixedMessages(HttpStatusCode status, string expected)
    {
        var response = new HttpResponseMessage(status) { Content = JsonContent.Create(new { title = "server detail", code = "x" }) };

        var message = DiagnosticsDisplay.TestNotificationMessage(await Client(response).SendTestAsync());

        Assert.Equal(expected, message);
        Assert.DoesNotContain("server detail", message);
    }

    [Fact]
    public async Task TransportFailure_IsTheGenericError()
    {
        var client = new NotificationsApiClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline"))) { BaseAddress = new("http://test/") });

        Assert.Equal(DiagnosticsDisplay.GenericError, DiagnosticsDisplay.TestNotificationMessage(await client.SendTestAsync()));
    }

    [Fact]
    public async Task UnreadableSuccessBody_IsTheGenericError()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") };

        Assert.Equal(TestNotificationStatus.Failed, (await Client(response).SendTestAsync()).Status);
    }

    // The request goes through AuthorizationMessageHandler with the session's token, and is bound
    // to the session that created it: after an account switch it is not sent with the new token.
    [Fact]
    public async Task SendTest_UsesTheAuthenticatedPipeline_BoundToItsSession()
    {
        var session = new TokenSession(new AuthApiClient(new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))) { BaseAddress = new("http://test/") }), new RefreshTokenStore());
        await session.EstablishAsync(new TokenResponse("account-a", "refresh-a", 900));
        var sentTokens = new List<string?>();
        var release = new TaskCompletionSource();
        var inner = new StubHandler(async request =>
        {
            sentTokens.Add(request.Headers.Authorization?.Parameter);
            await release.Task;
            return Ok(1, 1, 0);
        });
        var client = new NotificationsApiClient(new HttpClient(new AuthorizationMessageHandler(session, inner)) { BaseAddress = new("http://test/") }, session);

        var pending = client.SendTestAsync();
        await session.EstablishAsync(new TokenResponse("account-b", "refresh-b", 900));
        release.SetResult();

        Assert.Equal(TestNotificationStatus.Failed, (await pending).Status);
        Assert.Equal(["account-a"], sentTokens);
    }

    [Fact]
    public void NotificationsApiClient_IsRegisteredWithTheAuthorizedClientAndSession()
    {
        var program = File.ReadAllText(Path.Combine(ComponentsRoot(), "..", "MauiProgram.cs"));

        Assert.Contains(
            "builder.Services.AddSingleton(services => new NotificationsApiClient(CreateAuthorizedHttpClient(services), services.GetRequiredService<TokenSession>()));",
            program);
    }

    // ---- Helpers ----

    private static HttpResponseMessage Ok(int devices, int sent, int failed) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new TestNotificationResponse(devices, sent, failed)) };

    private static NotificationsApiClient Client(HttpResponseMessage response) =>
        new(new HttpClient(new StubHandler(_ => Task.FromResult(response))) { BaseAddress = new("http://test/") });

    private static string ComponentsRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components"));

    private static string Source(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(ComponentsRoot(), folder, fileName));

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
