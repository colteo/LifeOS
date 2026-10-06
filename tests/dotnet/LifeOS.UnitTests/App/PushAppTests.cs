using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LifeOS.App.Services.Auth;
using LifeOS.App.Services.Notifications;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Devices;
using LifeOS.Domain.Notifications;

namespace LifeOS.UnitTests.App;

// AUTO-001 WP3B in the app, without a device: installation id, the device-registration lifecycle
// (DeviceRegistrar), its transport and session binding (DevicesApiClient), notification tap parsing,
// and the MAUI/Android wiring (source checks). Physical push delivery is NOT proven here.
public class PushAppTests
{
    private static readonly Guid UserA = Guid.Parse("0192f0c3-0000-7000-8000-00000000000a");
    private static readonly Guid UserB = Guid.Parse("0192f0c3-0000-7000-8000-00000000000b");

    // ---- Installation id ----

    [Fact]
    public void InstallationId_IsCreatedOnce_AndReused()
    {
        string? stored = null;
        var saves = 0;

        var first = InstallationId.GetOrCreate(() => stored, value => { stored = value; saves++; });
        var second = InstallationId.GetOrCreate(() => stored, value => { stored = value; saves++; });

        Assert.Equal(first, second);
        Assert.Equal(1, saves);
        Assert.True(Guid.TryParse(first, out _));
        Assert.True(DeviceRegistration.IsValidInstallationId(first));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("has spaces in it 0000")]
    public void InstallationId_ReplacesAnUnusableStoredValue(string stored)
    {
        var replaced = InstallationId.GetOrCreate(() => stored, _ => { });

        Assert.NotEqual(stored, replaced);
        Assert.True(InstallationId.IsValid(replaced));
    }

    // ---- Registration request ----

    [Fact]
    public void Request_CarriesTheTokenOnlyWhenPermitted()
    {
        Assert.Equal(new RegisterDeviceRequest("Android", "token", true), DeviceRegistrar.Request(true, "token"));
        Assert.Equal(new RegisterDeviceRequest("Android", null, false), DeviceRegistrar.Request(false, "token"));
    }

    // ---- Lifecycle ----

    [Fact]
    public async Task Permitted_RegistersTheTokenForThisInstallation()
    {
        var harness = new RegistrarHarness { Token = "fcm-token" };

        await harness.Registrar.RegisterAsync(UserA);

        Assert.Equal([("installation-0001-abcdef", new RegisterDeviceRequest("Android", "fcm-token", true))], harness.Registered);
    }

    [Fact]
    public async Task Denied_RegistersNotPermitted_WithoutAToken()
    {
        var harness = new RegistrarHarness { Enabled = false, Token = "fcm-token" };

        await harness.Registrar.RegisterAsync(UserA);

        Assert.Equal(new RegisterDeviceRequest("Android", null, false), Assert.Single(harness.Registered).Request);
        Assert.Equal(0, harness.TokenReads);
    }

    [Fact]
    public async Task PermittedButNoToken_SendsNothing()
    {
        var harness = new RegistrarHarness { Token = null };

        await harness.Registrar.RegisterAsync(UserA);

        Assert.Empty(harness.Registered);
    }

    [Fact]
    public async Task ThePermissionPrompt_IsShownOncePerInstallation()
    {
        var harness = new RegistrarHarness { Token = "t" };

        await harness.Registrar.RegisterAsync(UserA);
        await harness.Registrar.RegisterAsync(UserA);

        Assert.Equal(1, harness.PermissionRequests);
        Assert.Equal(2, harness.Registered.Count);
    }

    [Fact]
    public async Task NotTheCurrentUser_SendsNothing()
    {
        var harness = new RegistrarHarness { Token = "t", CurrentUser = UserB };

        await harness.Registrar.RegisterAsync(UserA);

        Assert.Empty(harness.Registered);
        Assert.Equal(0, harness.PermissionRequests);
    }

    [Fact]
    public async Task AccountSwitchWhileTheTokenIsFetched_SendsNothingForTheOldAccount()
    {
        var harness = new RegistrarHarness();
        harness.TokenSource = () =>
        {
            harness.CurrentUser = UserB;
            return "t";
        };

        await harness.Registrar.RegisterAsync(UserA);

        Assert.Empty(harness.Registered);
    }

    [Fact]
    public async Task PlatformOrApiFailures_NeverThrow()
    {
        var throwing = new RegistrarHarness { TokenSource = () => throw new InvalidOperationException("Play services missing") };
        var failingApi = new RegistrarHarness { Token = "t", RegisterThrows = true };

        await throwing.Registrar.RegisterAsync(UserA);
        await failingApi.Registrar.RegisterAsync(UserA);
        await failingApi.Registrar.UnregisterAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SignOut_DeletesTheRegistration_ThenTheLocalToken()
    {
        var harness = new RegistrarHarness();

        await harness.Registrar.UnregisterAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(["delete:installation-0001-abcdef", "delete-token"], harness.Calls);
    }

    [Fact]
    public async Task SignOut_IsBoundedInTime_WhenTheApiHangs()
    {
        var harness = new RegistrarHarness { UnregisterHangs = true };
        var started = DateTime.UtcNow;

        await harness.Registrar.UnregisterAsync(TimeSpan.FromMilliseconds(300));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    // ---- Transport and session binding ----

    [Fact]
    public async Task DevicesApi_PutsTheRegistration_AndDeletes()
    {
        var requests = new List<(HttpMethod Method, string Uri, string? Body)>();
        var client = new DevicesApiClient(new HttpClient(new StubHandler(async request =>
        {
            requests.Add((request.Method, request.RequestUri!.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync()));
            return new HttpResponseMessage(request.Method == HttpMethod.Delete ? HttpStatusCode.NotFound : HttpStatusCode.NoContent);
        })) { BaseAddress = new("http://test/") });

        Assert.True(await client.RegisterAsync("installation-0001-abcdef", new RegisterDeviceRequest("Android", "t", true)));
        Assert.True(await client.UnregisterAsync("installation-0001-abcdef"));

        Assert.Equal((HttpMethod.Put, "http://test/api/devices/installation-0001-abcdef"), (requests[0].Method, requests[0].Uri));
        using var body = JsonDocument.Parse(requests[0].Body!);
        Assert.Equal(("Android", "t", true), (body.RootElement.GetProperty("platform").GetString(), body.RootElement.GetProperty("pushToken").GetString(), body.RootElement.GetProperty("notificationsPermitted").GetBoolean()));
        Assert.Equal(HttpMethod.Delete, requests[1].Method);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task DevicesApi_FailuresAreFalse(HttpStatusCode status)
    {
        var client = new DevicesApiClient(new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(status)))) { BaseAddress = new("http://test/") });
        var offline = new DevicesApiClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline"))) { BaseAddress = new("http://test/") });

        Assert.False(await client.RegisterAsync("installation-0001-abcdef", new RegisterDeviceRequest("Android", "t", true)));
        Assert.False(await offline.RegisterAsync("installation-0001-abcdef", new RegisterDeviceRequest("Android", "t", true)));
    }

    // A registration created for account A is never sent with account B's token (WP1 session binding).
    [Fact]
    public async Task DevicesApi_RequestIsBoundToTheSessionThatCreatedIt()
    {
        var session = new TokenSession(new AuthApiClient(new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))) { BaseAddress = new("http://test/") }), new RefreshTokenStore());
        await session.EstablishAsync(new TokenResponse("account-a", "refresh-a", 900));
        var sentTokens = new List<string?>();
        var release = new TaskCompletionSource();
        var inner = new StubHandler(async request =>
        {
            sentTokens.Add(request.Headers.Authorization?.Parameter);
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = new DevicesApiClient(new HttpClient(new AuthorizationMessageHandler(session, inner)) { BaseAddress = new("http://test/") }, session);

        var pending = client.RegisterAsync("installation-0001-abcdef", new RegisterDeviceRequest("Android", "t", true));
        await session.EstablishAsync(new TokenResponse("account-b", "refresh-b", 900));
        release.SetResult();

        Assert.False(await pending);
        Assert.Equal(["account-a"], sentTokens);
    }

    // ---- Notification taps ----

    [Fact]
    public void Tap_Test_OpensLifeOS()
    {
        Assert.Equal(new NotificationTarget(NotificationTargetKind.Test, null), NotificationTap.Parse("test", null));
        Assert.Null(NotificationTap.PathFor(NotificationTap.Parse("test", "ignored")!));
    }

    [Fact]
    public void Tap_WeeklyReview_NeedsAValidId_AndOpensThatReview()
    {
        var id = Guid.CreateVersion7();

        var target = NotificationTap.Parse("weekly_review", id.ToString());

        // AUTO-002 maps the target to the saved review's page (the page loads it through the API).
        Assert.Equal(new NotificationTarget(NotificationTargetKind.WeeklyReview, id), target);
        Assert.Equal($"weekly-reviews/{id:D}", NotificationTap.PathFor(target!));
        Assert.Null(NotificationTap.PathFor(new NotificationTarget(NotificationTargetKind.WeeklyReview, null)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("unknown", "x")]
    [InlineData("weekly_review", null)]
    [InlineData("weekly_review", "not-a-guid")]
    [InlineData("weekly_review", "00000000-0000-0000-0000-000000000000")]
    [InlineData("TEST", null)]
    public void Tap_UnknownOrMalformedData_IsIgnored(string? type, string? id)
    {
        Assert.Null(NotificationTap.Parse(type, id));
    }

    [Fact]
    public void PendingNavigation_IsTakenOnce_AndAnnounced()
    {
        var pending = new PendingNotificationNavigation();
        var announced = 0;
        pending.Changed += () => announced++;

        pending.Set(new NotificationTarget(NotificationTargetKind.Test, null));

        Assert.Equal(1, announced);
        Assert.NotNull(pending.TryTake());
        Assert.Null(pending.TryTake());
    }

    // ---- MAUI / Android wiring (source checks) ----

    [Fact]
    public void Android_ChannelAndPermission_MatchTheServer()
    {
        Assert.Contains("ChannelId = \"lifeos_general\"", AppSource("Platforms", "Android", "Notifications", "AndroidPushPlatform.cs"));
        Assert.Contains("NotificationImportance.Default", AppSource("Platforms", "Android", "Notifications", "AndroidPushPlatform.cs"));
        Assert.Contains("android.permission.POST_NOTIFICATIONS", AppSource("Platforms", "Android", "AndroidManifest.xml"));
        Assert.Contains("AndroidPushPlatform.EnsureChannel(this)", AppSource("Platforms", "Android", "MainApplication.cs"));
        Assert.Contains("android:allowBackup=\"false\"", AppSource("Platforms", "Android", "AndroidManifest.xml"));
    }

    [Fact]
    public void Android_TapsAreHandledWhenClosedAndWhenRunning()
    {
        var activity = AppSource("Platforms", "Android", "MainActivity.cs");

        Assert.Contains("LaunchMode = LaunchMode.SingleTop", activity);
        Assert.Contains("protected override void OnCreate(", activity);
        Assert.Contains("protected override void OnNewIntent(", activity);
        Assert.Equal(2, activity.Split("NotificationIntents.Consume(").Length - 1);
    }

    [Fact]
    public void App_RegistersAfterSignIn_OnResume_AndOnTokenChange()
    {
        var app = AppSource("App.xaml.cs");

        Assert.Contains("_auth.StateChanged += RegisterDevice;", app);
        Assert.Contains("window.Resumed += (_, _) => RegisterDevice();", app);
        Assert.Contains("PushTokenEvents.TokenChanged += RegisterDevice;", app);
        Assert.Contains("Task.Run(() => _devices.RegisterAsync(", app);
    }

    [Fact]
    public void SignOut_CleansUpPushBeforeClearingTheSession()
    {
        var auth = AppSource("Services", "Auth", "AuthService.cs");

        var cleanup = auth.IndexOf("await _devices.UnregisterAsync(SignOutPushCleanupTimeout);", StringComparison.Ordinal);
        var clear = auth.IndexOf("await _session.ClearAsync();", StringComparison.Ordinal);

        Assert.True(cleanup > 0 && cleanup < clear);
    }

    private static string AppSource(params string[] path) => AppSourceAt(Path.Combine(path));

    private static string AppSourceAt(string relativePath, [CallerFilePath] string testFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", relativePath)));

    private sealed class RegistrarHarness : IPushPlatform
    {
        private bool _permissionRequested;

        public RegistrarHarness()
        {
            Registrar = new DeviceRegistrar(
                this,
                () => "installation-0001-abcdef",
                () => _permissionRequested,
                () => _permissionRequested = true,
                (installationId, request, _) =>
                {
                    if (RegisterThrows)
                    {
                        throw new HttpRequestException();
                    }

                    Registered.Add((installationId, request));
                    return Task.FromResult(true);
                },
                async (installationId, cancellationToken) =>
                {
                    Calls.Add("delete:" + installationId);

                    if (UnregisterHangs)
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }

                    return true;
                },
                userId => userId == CurrentUser);
        }

        public DeviceRegistrar Registrar { get; }
        public Guid CurrentUser { get; set; } = UserA;
        public bool Enabled { get; init; } = true;
        public string? Token { get; init; }
        public Func<string?>? TokenSource { get; set; }
        public bool RegisterThrows { get; init; }
        public bool UnregisterHangs { get; init; }
        public int PermissionRequests { get; private set; }
        public int TokenReads { get; private set; }
        public List<(string InstallationId, RegisterDeviceRequest Request)> Registered { get; } = [];
        public List<string> Calls { get; } = [];

        public Task RequestPermissionAsync()
        {
            PermissionRequests++;
            return Task.CompletedTask;
        }

        public bool AreNotificationsEnabled() => Enabled;

        public Task<string?> GetTokenAsync()
        {
            TokenReads++;
            return Task.FromResult(TokenSource is null ? Token : TokenSource());
        }

        public Task DeleteTokenAsync()
        {
            Calls.Add("delete-token");
            return Task.CompletedTask;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
