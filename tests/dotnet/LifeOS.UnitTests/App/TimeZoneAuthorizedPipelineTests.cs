using System.Net;
using System.Net.Http.Json;
using LifeOS.App.Services.Auth;
using LifeOS.App.Services.Users;
using LifeOS.Contracts.Auth;

namespace LifeOS.UnitTests.App
{
    public class TimeZoneAuthorizedPipelineTests
    {
        [Theory]
        [InlineData(HttpStatusCode.NoContent, true, 0)]
        [InlineData(HttpStatusCode.NotFound, false, 0)]
        [InlineData(HttpStatusCode.ServiceUnavailable, false, 0)]
        [InlineData(HttpStatusCode.Unauthorized, false, 1)]
        public async Task SharedPipeline_RefreshesOnce_AndHandlesFinalStatus(HttpStatusCode finalStatus, bool accepted, int ended)
        {
            var refreshes = 0;
            var session = Session(_ =>
            {
                refreshes++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Tokens("refreshed")) });
            });
            await session.EstablishAsync(Tokens("original"));
            var sessionEnds = 0;
            session.SessionEnded += _ => sessionEnds++;
            var requests = new List<string?>();
            var client = Client(session, request =>
            {
                requests.Add(request.Headers.Authorization?.Parameter);
                return Task.FromResult(new HttpResponseMessage(requests.Count == 1 ? HttpStatusCode.Unauthorized : finalStatus));
            });

            Assert.Equal(accepted, await client.SetTimeZoneAsync("Europe/Rome"));
            Assert.Equal(["original", "refreshed"], requests);
            Assert.Equal(1, refreshes);
            Assert.Equal(ended, sessionEnds);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, 1)]
        [InlineData(HttpStatusCode.ServiceUnavailable, 0)]
        public async Task RefreshFailure_IsNotAcknowledged(HttpStatusCode refreshStatus, int ended)
        {
            var session = Session(_ => Task.FromResult(new HttpResponseMessage(refreshStatus)));
            await session.EstablishAsync(Tokens("original"));
            var sessionEnds = 0;
            session.SessionEnded += _ => sessionEnds++;
            var requests = 0;
            var client = Client(session, _ => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); });

            Assert.False(await client.SetTimeZoneAsync("Europe/Rome"));
            Assert.Equal(1, requests);
            Assert.Equal(ended, sessionEnds);
            Assert.Equal(ended == 1 ? null : "original", session.AccessToken);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.NoContent)]
        public async Task AccountChangedInFlight_DoesNotRetryWithNewAccountsToken_OrAcknowledgeOldAccount(HttpStatusCode status)
        {
            var refreshes = 0;
            var session = Session(_ => { refreshes++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); });
            await session.EstablishAsync(Tokens("account-a"));
            var release = new TaskCompletionSource<HttpResponseMessage>();
            var requests = new List<string?>();
            var client = Client(session, request => { requests.Add(request.Headers.Authorization?.Parameter); return release.Task; });
            var pending = client.SetTimeZoneAsync("Europe/Rome");
            await session.ClearAsync();
            await session.EstablishAsync(Tokens("account-b"));
            release.SetResult(new HttpResponseMessage(status));

            Assert.False(await pending);
            Assert.Equal(["account-a"], requests);
            Assert.Equal(0, refreshes);
            Assert.Equal("account-b", session.AccessToken);
        }

        private static TokenResponse Tokens(string access) => new(access, "synthetic-refresh", 3600);

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized)]
        [InlineData(HttpStatusCode.NoContent)]
        public async Task AccountChangedDuringRetry_OldResponseCannotEndNewSessionOrAcknowledge(HttpStatusCode status)
        {
            var session = Session(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Tokens("refreshed-a")) }));
            await session.EstablishAsync(Tokens("account-a"));
            var release = new TaskCompletionSource<HttpResponseMessage>();
            var requests = 0;
            var ended = 0;
            session.SessionEnded += _ => ended++;
            var client = Client(session, _ => ++requests == 1
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)) : release.Task);

            var pending = client.SetTimeZoneAsync("Europe/Rome");
            Assert.Equal(2, requests);
            await session.ClearAsync();
            await session.EstablishAsync(Tokens("account-b"));
            release.SetResult(new HttpResponseMessage(status));

            Assert.False(await pending);
            Assert.Equal("account-b", session.AccessToken);
            Assert.Equal(0, ended);
        }

        [Fact]
        public async Task StaleSessionVersion_CannotRefreshOrEndTheNewSession()
        {
            var refreshes = 0;
            var session = Session(_ => { refreshes++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); });
            await session.EstablishAsync(Tokens("account-a"));
            var oldVersion = session.Version;
            await session.EstablishAsync(Tokens("account-b"));
            var ended = 0;
            session.SessionEnded += _ => ended++;

            Assert.Equal(SessionUpdate.Unavailable, await session.RefreshAsync("account-a", expectedVersion: oldVersion));
            await session.EndAsync("stale response", oldVersion);
            Assert.Equal("account-b", session.AccessToken);
            Assert.Equal(0, refreshes);
            Assert.Equal(0, ended);
        }
        private static TokenSession Session(Func<HttpRequestMessage, Task<HttpResponseMessage>> refresh) =>
            new(new AuthApiClient(new HttpClient(new StubHandler(refresh)) { BaseAddress = new("http://test/") }), new RefreshTokenStore());
        private static MeApiClient Client(TokenSession session, Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
            new(new HttpClient(new AuthorizationMessageHandler(session, new StubHandler(send))) { BaseAddress = new("http://test/") }, session);
        private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
        }
    }
}

namespace LifeOS.App.Services.Auth
{
    // Test double for MAUI SecureStorage only. TokenSession and the HTTP handler are production code.
    public sealed class RefreshTokenStore
    {
        private string? _token;
        public Task<string?> GetAsync() => Task.FromResult(_token);
        public Task<bool> TrySaveAsync(string token) { _token = token; return Task.FromResult(true); }
        public void Clear() => _token = null;
    }
}
