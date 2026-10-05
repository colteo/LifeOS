using System.Net;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using LifeOS.Api.Notifications;
using LifeOS.Application.Notifications;
using LifeOS.Domain.Notifications;
using LifeOS.Infrastructure.Notifications.Fcm;
using LifeOS.UnitTests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LifeOS.UnitTests.Notifications;

// AUTO-001 WP3B server side: FCM configuration rules, typed service-account loading, the HTTP v1
// request and the mapping of FCM responses to PushSendResult. No test contacts Google: the HTTP
// handler and the access-token source are fakes.
public class FcmPushNotificationSenderTests
{
    private const string Token = "device-token-SENSITIVE";
    private static readonly PushTarget Target = new(PushProvider.Fcm, Token);
    private static readonly PushMessage Message = new(
        "LifeOS", "Test notification from LifeOS", new Dictionary<string, string> { ["type"] = "test" }, "test:0192f0c3-0000-7000-8000-000000000001");

    // ---- Configuration (open question 3) ----

    [Fact]
    public void NoFcmConfiguration_DisablesPush()
    {
        Assert.Null(PushNotificationsConfiguration.ReadFcm(Configuration(null, null)));
        Assert.Null(PushNotificationsConfiguration.ReadFcm(Configuration(" ", "")));
    }

    [Theory]
    [InlineData("lifeos-test-project", null)]
    [InlineData(null, "e30=")]
    public void PartialFcmConfiguration_FailsStartup(string? projectId, string? json)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PushNotificationsConfiguration.ReadFcm(Configuration(projectId, json)));

        Assert.Contains("configured together", exception.Message);
    }

    [Theory]
    [InlineData("Bad_Project")]
    [InlineData("abc")]
    [InlineData("project-")]
    public void MalformedProjectId_FailsStartup(string projectId)
    {
        Assert.Throws<InvalidOperationException>(() =>
            PushNotificationsConfiguration.ReadFcm(Configuration(projectId, TestServiceAccount.Base64(TestServiceAccount.Json()))));
    }

    [Theory]
    [InlineData("not base64 !!!")]
    [InlineData("bm90IGpzb24=")]        // "not json"
    [InlineData("WzEsMl0=")]            // [1,2]: JSON, not an object
    [InlineData("//79")]                // invalid UTF-8
    public void MalformedServiceAccountValue_FailsStartup_WithoutQuotingIt(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PushNotificationsConfiguration.ReadFcm(Configuration(TestServiceAccount.ProjectId, value)));

        Assert.Contains(PushNotificationsConfiguration.ServiceAccountJsonKey, exception.Message);
        Assert.DoesNotContain(value, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void ValidConfiguration_DecodesTheJson_AndNeverPrintsIt()
    {
        var options = PushNotificationsConfiguration.ReadFcm(Configuration(TestServiceAccount.ProjectId, TestServiceAccount.Base64(TestServiceAccount.Json())));

        Assert.NotNull(options);
        Assert.Equal(TestServiceAccount.ProjectId, options.ProjectId);
        Assert.Equal(TestServiceAccount.Json(), options.ServiceAccountJson);
        Assert.DoesNotContain("private_key", options.ToString());
    }

    [Fact]
    public void ServiceAccountKey_LoadsAsAScopedCredential()
    {
        Assert.IsType<GoogleCredential>(FcmCredentials.Load(TestServiceAccount.Json()));
        Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", FcmCredentials.MessagingScope);
    }

    [Theory]
    [MemberData(nameof(NotServiceAccounts))]
    public void NonServiceAccountCredential_IsRejected_WithoutLeakingIt(string json)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => FcmCredentials.Load(json));

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("PRIVATE KEY", exception.Message);
        Assert.DoesNotContain(TestServiceAccount.ClientEmail, exception.Message);
        Assert.DoesNotContain("not-a-secret", exception.Message);
    }

    public static TheoryData<string> NotServiceAccounts => new()
    {
        TestServiceAccount.AuthorizedUserJson(),
        TestServiceAccount.Json(type: "external_account"),
        """{"type":"service_account"}""",
        "{}"
    };

    // ---- Request ----

    [Fact]
    public async Task Send_PostsTheHttpV1Message_WithTheBearerToken()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        var sender = Sender(async request =>
        {
            sent = request;
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """{"name":"projects/lifeos-test-project/messages/1"}""");
        });

        Assert.Equal(PushSendResult.Accepted, await sender.SendAsync(Target, Message, default));

        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal("https://fcm.googleapis.com/v1/projects/lifeos-test-project/messages:send", sent.RequestUri!.ToString());
        Assert.Equal(("Bearer", "oauth-access-token"), (sent.Headers.Authorization!.Scheme, sent.Headers.Authorization.Parameter));

        using var json = JsonDocument.Parse(body!);
        var message = json.RootElement.GetProperty("message");
        Assert.Equal(["token", "notification", "data", "android"], message.EnumerateObject().Select(property => property.Name));
        Assert.Equal(Token, message.GetProperty("token").GetString());
        Assert.Equal("""{"title":"LifeOS","body":"Test notification from LifeOS"}""", message.GetProperty("notification").GetRawText());
        Assert.Equal("""{"type":"test"}""", message.GetProperty("data").GetRawText());
        Assert.Equal(
            """{"priority":"normal","notification":{"tag":"test:0192f0c3-0000-7000-8000-000000000001","channel_id":"lifeos_general"}}""",
            message.GetProperty("android").GetRawText());
    }

    // ---- Outcome mapping ----

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "UNREGISTERED", null, PushSendResult.TokenInvalid)]
    [InlineData(HttpStatusCode.Forbidden, "SENDER_ID_MISMATCH", null, PushSendResult.TokenInvalid)]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_ARGUMENT", "message.token", PushSendResult.TokenInvalid)]
    [InlineData(HttpStatusCode.BadRequest, "INVALID_ARGUMENT", "message.android.notification.tag", PushSendResult.Rejected)]
    [InlineData(HttpStatusCode.BadRequest, null, null, PushSendResult.Rejected)]
    [InlineData(HttpStatusCode.NotFound, null, null, PushSendResult.Rejected)]        // e.g. a wrong project id: not the device's fault
    [InlineData(HttpStatusCode.Forbidden, null, null, PushSendResult.Rejected)]       // missing IAM permission: not the device's fault
    [InlineData(HttpStatusCode.Unauthorized, null, null, PushSendResult.Transient)]   // our OAuth token: not the device's fault
    [InlineData(HttpStatusCode.TooManyRequests, "QUOTA_EXCEEDED", null, PushSendResult.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, null, null, PushSendResult.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, "INTERNAL", null, PushSendResult.Transient)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "UNAVAILABLE", null, PushSendResult.Transient)]
    [InlineData(HttpStatusCode.BadGateway, null, null, PushSendResult.Transient)]
    public async Task ProviderResponses_MapToStableOutcomes(HttpStatusCode status, string? errorCode, string? field, PushSendResult expected)
    {
        var logs = new ListLogger();
        var sender = Sender(_ => Task.FromResult(Json(status, ErrorBody(status, errorCode, field))), logs);

        Assert.Equal(expected, await sender.SendAsync(Target, Message, default));
        Assert.All(logs.Messages, message =>
        {
            Assert.DoesNotContain(Token, message);
            Assert.DoesNotContain("provider says", message);
        });
    }

    [Fact]
    public async Task NonJsonErrorBody_IsStillClassifiedByStatus()
    {
        var sender = Sender(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("<html>down</html>") }));

        Assert.Equal(PushSendResult.Transient, await sender.SendAsync(Target, Message, default));
    }

    [Fact]
    public async Task TransportFailureAndTimeout_AreTransient()
    {
        Assert.Equal(PushSendResult.Transient, await Sender(_ => throw new HttpRequestException("connection reset")).SendAsync(Target, Message, default));
        Assert.Equal(PushSendResult.Transient, await Sender(_ => throw new TaskCanceledException("timeout")).SendAsync(Target, Message, default));
    }

    [Fact]
    public async Task CredentialFailure_IsTransient_AndSendsNothing()
    {
        var calls = 0;
        var sender = new FcmPushNotificationSender(
            new HttpClient(new StubHandler(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); })) { BaseAddress = FcmPushNotificationSender.BaseAddress },
            new FakeTokenAccess(() => throw new InvalidOperationException("invalid_grant for " + TestServiceAccount.ClientEmail)),
            TestServiceAccount.ProjectId,
            new ListLogger());

        Assert.Equal(PushSendResult.Transient, await sender.SendAsync(Target, Message, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task EachSend_IsExactlyOneHttpAttempt()
    {
        var calls = 0;
        var sender = Sender(_ => { calls++; return Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, "{}")); });

        await sender.SendAsync(Target, Message, default);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void HttpClientSettings_AreBounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), FcmPushNotificationSender.Timeout);
        Assert.Equal("https://fcm.googleapis.com/", FcmPushNotificationSender.BaseAddress.ToString());
    }

    private static FcmPushNotificationSender Sender(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, ListLogger? logs = null) =>
        new(
            new HttpClient(new StubHandler(respond)) { BaseAddress = FcmPushNotificationSender.BaseAddress },
            new FakeTokenAccess(() => "oauth-access-token"),
            TestServiceAccount.ProjectId,
            logs ?? new ListLogger());

    private static string ErrorBody(HttpStatusCode status, string? errorCode, string? field)
    {
        var details = new List<object>();

        if (errorCode is not null)
        {
            details.Add(new Dictionary<string, object> { ["@type"] = "type.googleapis.com/google.firebase.fcm.v1.FcmError", ["errorCode"] = errorCode });
        }

        if (field is not null)
        {
            details.Add(new Dictionary<string, object>
            {
                ["@type"] = "type.googleapis.com/google.rpc.BadRequest",
                ["fieldViolations"] = new[] { new Dictionary<string, string> { ["field"] = field, ["description"] = "provider says " + Token } }
            });
        }

        return JsonSerializer.Serialize(new { error = new { code = (int)status, message = "provider says " + Token, status = "X", details } });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static IConfiguration Configuration(string? projectId, string? json) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PushNotificationsConfiguration.ProjectIdKey] = projectId,
                [PushNotificationsConfiguration.ServiceAccountJsonKey] = json
            })
            .Build();

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class FakeTokenAccess(Func<string> token) : ITokenAccess
    {
        public Task<string> GetAccessTokenForRequestAsync(string? authUri = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(token());
    }

    private sealed class ListLogger : ILogger<FcmPushNotificationSender>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + exception);
    }
}
