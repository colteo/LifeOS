using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LifeOS.App.Services.Users;

namespace LifeOS.UnitTests.App;

// AUTO-001 WP1 in the app: the device-following time zone sync (TimeZoneSynchronizer), its transport
// (MeApiClient.SetTimeZoneAsync) and the device zone choice (DeviceTimeZone). The MAUI wiring
// (App.xaml.cs) cannot run here; those checks read the source.
public class TimeZoneSyncAppTests
{
    private static readonly Guid UserId = Guid.Parse("0192f0c3-0000-7000-8000-000000000001");
    private static readonly Guid OtherUserId = Guid.Parse("0192f0c3-0000-7000-8000-000000000002");

    // ---- TimeZoneSynchronizer ----

    [Fact]
    public async Task SameAcknowledgedZone_SendsNoRequest()
    {
        var sync = new SyncHarness("Europe/Rome") { Acknowledged = TimeZoneSynchronizer.Acknowledgement(UserId, "Europe/Rome") };

        await sync.Synchronizer.SynchronizeAsync(UserId);

        Assert.Empty(sync.Sent);
    }

    [Fact]
    public async Task NoAcknowledgement_SendsZone_AndCachesItOnSuccess()
    {
        var sync = new SyncHarness("Europe/Rome");

        await sync.Synchronizer.SynchronizeAsync(UserId);

        Assert.Equal(["Europe/Rome"], sync.Sent);
        Assert.Equal(TimeZoneSynchronizer.Acknowledgement(UserId, "Europe/Rome"), sync.Acknowledged);
    }

    [Fact]
    public async Task ChangedDeviceZone_IsSentEvenAfterAnOlderAcknowledgement()
    {
        var sync = new SyncHarness("America/New_York") { Acknowledged = TimeZoneSynchronizer.Acknowledgement(UserId, "Europe/Rome") };

        await sync.Synchronizer.SynchronizeAsync(UserId);

        Assert.Equal(["America/New_York"], sync.Sent);
        Assert.Equal(TimeZoneSynchronizer.Acknowledgement(UserId, "America/New_York"), sync.Acknowledged);
    }

    [Fact]
    public async Task AnotherUserOnTheSameDevice_StillSendsTheZone()
    {
        var sync = new SyncHarness("Europe/Rome") { Acknowledged = TimeZoneSynchronizer.Acknowledgement(OtherUserId, "Europe/Rome") };

        await sync.Synchronizer.SynchronizeAsync(UserId);

        Assert.Equal(["Europe/Rome"], sync.Sent);
    }

    [Fact]
    public async Task NotAcknowledged_KeepsThePreviousAcknowledgement_AndRetriesNextTime()
    {
        var previous = TimeZoneSynchronizer.Acknowledgement(UserId, "Europe/Rome");
        var sync = new SyncHarness("America/New_York") { Acknowledged = previous, Accept = false };

        await sync.Synchronizer.SynchronizeAsync(UserId);
        Assert.Equal(previous, sync.Acknowledged);

        sync.Accept = true;
        await sync.Synchronizer.SynchronizeAsync(UserId);

        Assert.Equal(["America/New_York", "America/New_York"], sync.Sent);
        Assert.Equal(TimeZoneSynchronizer.Acknowledgement(UserId, "America/New_York"), sync.Acknowledged);
    }

    [Fact]
    public async Task SendThrows_DoesNotPropagate_AndKeepsTheAcknowledgement()
    {
        var previous = TimeZoneSynchronizer.Acknowledgement(UserId, "Europe/Rome");
        var sync = new SyncHarness("America/New_York") { Acknowledged = previous, Throw = new InvalidOperationException() };

        await sync.Synchronizer.SynchronizeAsync(UserId);

        Assert.Equal(previous, sync.Acknowledged);
    }

    [Fact]
    public async Task ReadingTheDeviceZoneThrows_DoesNotPropagate()
    {
        var sent = 0;
        var synchronizer = new TimeZoneSynchronizer(
            () => throw new InvalidOperationException(),
            () => null,
            _ => { },
            (_, _) => { sent++; return Task.FromResult(true); });

        await synchronizer.SynchronizeAsync(UserId);

        Assert.Equal(0, sent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task UnknownDeviceZone_SendsNothing(string? zone)
    {
        var sync = new SyncHarness(zone);

        await sync.Synchronizer.SynchronizeAsync(UserId);

        Assert.Empty(sync.Sent);
        Assert.Null(sync.Acknowledged);
    }

    [Fact]
    public async Task ConcurrentTriggers_SendOnce()
    {
        var release = new TaskCompletionSource<bool>();
        var sent = 0;
        string? acknowledged = null;
        var synchronizer = new TimeZoneSynchronizer(
            () => "Europe/Rome",
            () => acknowledged,
            value => acknowledged = value,
            (_, _) => { sent++; return release.Task; });

        var first = synchronizer.SynchronizeAsync(UserId);
        var second = synchronizer.SynchronizeAsync(UserId);
        release.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task ZoneChangesDuringRequest_QueuedTriggerSendsLatestZone()
    {
        var release = new TaskCompletionSource<bool>();
        var zone = "Europe/Rome";
        string? acknowledged = null;
        var sent = new List<string>();
        var synchronizer = new TimeZoneSynchronizer(() => zone, () => acknowledged, value => acknowledged = value,
            (value, _) => { sent.Add(value); return sent.Count == 1 ? release.Task : Task.FromResult(true); });

        var first = synchronizer.SynchronizeAsync(UserId);
        zone = "Asia/Tokyo";
        var second = synchronizer.SynchronizeAsync(UserId);
        Assert.Single(sent);
        release.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(["Europe/Rome", "Asia/Tokyo"], sent);
        Assert.Equal(TimeZoneSynchronizer.Acknowledgement(UserId, zone), acknowledged);
    }

    [Fact]
    public async Task AccountChangesDuringRequest_OldCompletionIsNotCached_AndNewUserIsSent()
    {
        var release = new TaskCompletionSource<bool>();
        var currentUser = UserId;
        string? acknowledged = null;
        var saved = new List<string>();
        var sent = 0;
        var synchronizer = new TimeZoneSynchronizer(() => "Europe/Rome", () => acknowledged,
            value => { acknowledged = value; saved.Add(value); },
            (_, _) => ++sent == 1 ? release.Task : Task.FromResult(true), id => id == currentUser);

        var first = synchronizer.SynchronizeAsync(UserId);
        var stale = synchronizer.SynchronizeAsync(UserId);
        currentUser = OtherUserId;
        var second = synchronizer.SynchronizeAsync(OtherUserId);
        release.SetResult(true);
        await Task.WhenAll(first, stale, second);

        Assert.Equal(2, sent);
        Assert.Equal([TimeZoneSynchronizer.Acknowledgement(OtherUserId, "Europe/Rome")], saved);
    }

    [Fact]
    public async Task CancelledQueuedTrigger_DoesNotReleaseAnotherAttemptsLock()
    {
        var release = new TaskCompletionSource<bool>();
        var sent = 0;
        string? acknowledged = null;
        var synchronizer = new TimeZoneSynchronizer(() => "Europe/Rome", () => acknowledged,
            value => acknowledged = value, (_, _) => { sent++; return release.Task; });
        var first = synchronizer.SynchronizeAsync(UserId);
        using var cancellation = new CancellationTokenSource();
        var second = synchronizer.SynchronizeAsync(UserId, cancellation.Token);
        cancellation.Cancel();
        await second;
        var third = synchronizer.SynchronizeAsync(UserId);
        Assert.Equal(1, sent);
        release.SetResult(true);
        await Task.WhenAll(first, third);
        Assert.Equal(1, sent);
    }

    // ---- MeApiClient.SetTimeZoneAsync (transport) ----

    [Fact]
    public async Task SetTimeZone_PutsTheZone_AndIsAcknowledgedOn204()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        var handler = new StubHandler(async request =>
        {
            sent = request;
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        Assert.True(await Client(handler).SetTimeZoneAsync("Europe/Rome"));
        Assert.Equal(HttpMethod.Put, sent!.Method);
        Assert.Equal("http://test/api/me/time-zone", sent.RequestUri!.ToString());
        Assert.Equal("Europe/Rome", JsonDocument.Parse(body!).RootElement.GetProperty("timeZoneId").GetString());
    }

    // 401 and 404 are "not acknowledged" for the sync, never a reason to end the session (unlike GET /api/me).
    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.Created)]
    public async Task SetTimeZone_AnyFailureStatus_IsNotAcknowledged(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(status)));

        Assert.False(await Client(handler).SetTimeZoneAsync("Europe/Rome"));
    }

    [Fact]
    public async Task SetTimeZone_TransportFailure_IsNotAcknowledged()
    {
        var network = new StubHandler(_ => throw new HttpRequestException("offline"));
        var timeout = new StubHandler(_ => throw new TaskCanceledException());

        Assert.False(await Client(network).SetTimeZoneAsync("Europe/Rome"));
        Assert.False(await Client(timeout).SetTimeZoneAsync("Europe/Rome"));
    }

    [Fact]
    public async Task EndToEnd_400_DoesNotCacheTheZone()
    {
        var previous = TimeZoneSynchronizer.Acknowledgement(UserId, "Europe/Rome");
        var acknowledged = previous;
        var client = Client(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))));
        var synchronizer = new TimeZoneSynchronizer(() => "Mars/Olympus_Mons", () => acknowledged, value => acknowledged = value, client.SetTimeZoneAsync);

        await synchronizer.SynchronizeAsync(UserId);

        Assert.Equal(previous, acknowledged);
    }

    [Fact]
    public async Task EndToEnd_TransportFailure_DoesNotCacheTheZone()
    {
        string? acknowledged = null;
        var client = Client(new StubHandler(_ => throw new HttpRequestException("offline")));
        var synchronizer = new TimeZoneSynchronizer(() => "Europe/Rome", () => acknowledged, value => acknowledged = value, client.SetTimeZoneAsync);

        await synchronizer.SynchronizeAsync(UserId);

        Assert.Null(acknowledged);
    }

    // ---- DeviceTimeZone ----

    [Fact]
    public void DeviceZone_PrefersAnIanaLocalZone()
    {
        var rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");

        Assert.Equal("Europe/Rome", DeviceTimeZone.Resolve(rome, () => "Asia/Tokyo"));
    }

    [Fact]
    public void DeviceZone_FallsBackToThePlatformId_WhenLocalIsNotIana()
    {
        var custom = TimeZoneInfo.CreateCustomTimeZone("Local", TimeSpan.FromHours(2), "Local", "Local");

        Assert.Equal("Europe/Rome", DeviceTimeZone.Resolve(custom, () => " Europe/Rome "));
        Assert.Null(DeviceTimeZone.Resolve(custom, () => null));
    }

    // ---- MAUI wiring (source checks) ----

    [Fact]
    public void App_SynchronizesAfterSignInAndOnResume_InTheBackground()
    {
        var app = AppSource("App.xaml.cs");

        Assert.Contains("_auth.StateChanged += SynchronizeTimeZone;", app);
        Assert.Contains("window.Resumed +=", app);
        Assert.Contains("_auth.State == AuthState.Authenticated", app);
        Assert.Contains("Task.Run(() => _timeZone.SynchronizeAsync(", app);
    }

    [Fact]
    public void Synchronizer_NeverTouchesTheSession()
    {
        var sync = AppSource(Path.Combine("Services", "Users", "TimeZoneSynchronizer.cs"));

        Assert.DoesNotContain("AuthService", sync);
        Assert.DoesNotContain("TokenSession", sync);
    }

    private static MeApiClient Client(HttpMessageHandler handler) => new(new HttpClient(handler) { BaseAddress = new("http://test/") });

    private static string AppSource(string relativePath, [CallerFilePath] string testFile = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", relativePath)));

    private sealed class SyncHarness
    {
        public SyncHarness(string? deviceZone)
        {
            Synchronizer = new TimeZoneSynchronizer(
                () => deviceZone,
                () => Acknowledged,
                value => Acknowledged = value,
                (zone, _) =>
                {
                    Sent.Add(zone);
                    return Throw is not null ? Task.FromException<bool>(Throw) : Task.FromResult(Accept);
                });
        }

        public TimeZoneSynchronizer Synchronizer { get; }
        public string? Acknowledged { get; set; }
        public bool Accept { get; set; } = true;
        public Exception? Throw { get; set; }
        public List<string> Sent { get; } = [];
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }
}
