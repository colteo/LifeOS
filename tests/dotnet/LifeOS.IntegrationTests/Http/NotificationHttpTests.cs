using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LifeOS.Api.Notifications;
using LifeOS.Application.Notifications;
using LifeOS.Contracts.Notifications;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Users;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LifeOS.IntegrationTests.Http;

// AUTO-001 WP3B: FCM configuration at startup, and POST /api/notifications/test.
public class NotificationHttpTests
{
    private const string Path = NotificationEndpoints.TestPath;

    // ---- FCM configuration at startup ----

    [Fact]
    public async Task NoFcmConfiguration_StartsWithPushDisabled()
    {
        await using var factory = new LifeOSApiFactory();
        factory.CreateClient();

        Assert.Null(factory.Services.GetService<IPushNotificationSender>());
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().IsEnabled);
    }

    [Fact]
    public async Task ValidFcmConfiguration_RegistersExactlyOneFcmSender()
    {
        await using var factory = WithFcm(TestServiceAccount.ProjectId, TestServiceAccount.Base64(TestServiceAccount.Json()));
        factory.CreateClient();

        var sender = Assert.Single(factory.Services.GetServices<IPushNotificationSender>());
        Assert.Equal("FcmPushNotificationSender", sender.GetType().Name);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.True(scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().IsEnabled);
    }

    [Theory]
    [MemberData(nameof(InvalidFcmConfigurations))]
    public async Task PartialOrInvalidFcmConfiguration_FailsStartup_WithoutLeakingTheKey(string projectId, string json)
    {
        await using var factory = WithFcm(projectId, json);

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var message = FullMessage(exception);

        Assert.Contains("Notifications:Fcm", message);
        Assert.DoesNotContain("PRIVATE KEY", message);
        Assert.DoesNotContain(TestServiceAccount.ClientEmail, message);
    }

    public static TheoryData<string, string> InvalidFcmConfigurations => new()
    {
        { TestServiceAccount.ProjectId, "" },
        { "", TestServiceAccount.Base64(TestServiceAccount.Json()) },
        { TestServiceAccount.ProjectId, "%%% not base64 %%%" },
        { TestServiceAccount.ProjectId, TestServiceAccount.Base64("not json") },
        { TestServiceAccount.ProjectId, TestServiceAccount.Base64(TestServiceAccount.AuthorizedUserJson()) },
        { "Not_A_Project", TestServiceAccount.Base64(TestServiceAccount.Json()) }
    };

    // ---- POST /api/notifications/test ----

    [Fact]
    public async Task Anonymous_Returns401()
    {
        await using var factory = WithSender(new FakePushNotificationSender());

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsync(Path, null)).StatusCode);
    }

    [Fact]
    public async Task BodyOrQuery_Returns400()
    {
        await using var factory = WithSender(new FakePushNotificationSender());
        var (first, _) = SignedIn(factory);
        var (second, _) = SignedIn(factory);

        var withBody = await first.PostAsync(Path, new StringContent("""{"title":"hi","token":"x"}""", Encoding.UTF8, "application/json"));
        var withQuery = await second.PostAsync(Path + "?userId=" + Guid.CreateVersion7(), null);

        Assert.Equal(HttpStatusCode.BadRequest, withBody.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, withQuery.StatusCode);
        Assert.Empty(factory.NotificationDeliveries.Rows);
    }

    [Fact]
    public async Task NoActiveDevice_Returns409NoActiveDevice()
    {
        await using var factory = WithSender(new FakePushNotificationSender());
        var (client, userId) = SignedIn(factory);
        await RegisterAsync(factory, userId, "phone-installation-0001", null);

        var response = await client.PostAsync(Path, null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("no_active_device", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        Assert.Empty(factory.NotificationDeliveries.Rows);
    }

    [Fact]
    public async Task PushDisabled_Returns503()
    {
        await using var factory = new LifeOSApiFactory();
        var (client, userId) = SignedIn(factory);
        await RegisterAsync(factory, userId, "phone-installation-0001", "token-a");

        var response = await client.PostAsync(Path, null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(factory.NotificationDeliveries.Rows);
    }

    [Fact]
    public async Task OneDevice_IsSent_AndTheResponseCarriesCountsOnly()
    {
        var sender = new FakePushNotificationSender();
        await using var factory = WithSender(sender);
        var (client, userId) = SignedIn(factory);
        await RegisterAsync(factory, userId, "phone-installation-0001", "token-SECRET-a");

        var response = await client.PostAsync(Path, null);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"devices":1,"sent":1,"failed":0}""", json);
        Assert.DoesNotContain("SECRET", json);

        var (target, message) = Assert.Single(sender.Sent);
        Assert.Equal(("token-SECRET-a", "LifeOS", "Test notification from LifeOS"), (target.Token, message.Title, message.Body));
        var row = Assert.Single(factory.NotificationDeliveries.Rows);
        Assert.Equal((userId, NotificationType.Test, NotificationDeliveryStatus.Sent), (row.UserId, row.Type, row.Status));
        Assert.Equal(TimeSpan.FromMinutes(15), row.ExpiresAtUtc - row.CreatedAtUtc);
    }

    [Fact]
    public async Task SeveralDevices_MixedOutcomes_AreCounted_AndOtherUsersAreNeverTargeted()
    {
        var sender = new FakePushNotificationSender
        {
            Respond = (target, _) => Task.FromResult(target.Token switch
            {
                "token-ok" => PushSendResult.Accepted,
                "token-retry" => PushSendResult.Transient,
                _ => PushSendResult.Rejected
            })
        };
        await using var factory = WithSender(sender);
        var (client, userId) = SignedIn(factory);
        var (_, otherId) = SignedIn(factory);
        await RegisterAsync(factory, userId, "phone-installation-0001", "token-ok");
        await RegisterAsync(factory, userId, "tablet-installation-01", "token-retry");
        await RegisterAsync(factory, userId, "watch-installation-001", "token-rejected");
        await RegisterAsync(factory, otherId, "other-installation-01", "token-other");

        var response = await client.PostAsync(Path, null);

        Assert.Equal("""{"devices":3,"sent":1,"failed":2}""", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(sender.Sent, sent => sent.Target.Token == "token-other");
        Assert.Equal(3, factory.NotificationDeliveries.Rows.Count);
        Assert.All(factory.NotificationDeliveries.Rows, row => Assert.Equal(userId, row.UserId));
    }

    [Fact]
    public async Task RateLimit_AllowsOnePerMinutePerUser()
    {
        await using var factory = WithSender(new FakePushNotificationSender());
        var (client, userId) = SignedIn(factory);
        var (other, otherId) = SignedIn(factory);
        await RegisterAsync(factory, userId, "phone-installation-0001", "token-a");
        await RegisterAsync(factory, otherId, "other-installation-01", "token-b");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(Path, null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync(Path, null)).StatusCode);

        // Per user: another user is not affected.
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsync(Path, null)).StatusCode);
        Assert.Equal(2, factory.NotificationDeliveries.Rows.Count);
    }

    private static LifeOSApiFactory WithSender(FakePushNotificationSender sender) =>
        new(configure: builder => builder.ConfigureTestServices(services => services.AddSingleton<IPushNotificationSender>(sender)));

    private static LifeOSApiFactory WithFcm(string projectId, string json) =>
        new(configure: builder =>
        {
            builder.UseSetting(PushNotificationsConfiguration.ProjectIdKey, projectId);
            builder.UseSetting(PushNotificationsConfiguration.ServiceAccountJsonKey, json);
        });

    private static Task<bool> RegisterAsync(LifeOSApiFactory factory, Guid userId, string installationId, string? token) =>
        factory.Devices.UpsertAsync(
            DeviceRegistration.Register(userId, installationId, DevicePlatform.Android, token, token is not null, DateTimeOffset.UtcNow), default);

    private static (HttpClient Client, Guid UserId) SignedIn(LifeOSApiFactory factory)
    {
        var client = factory.CreateClient();
        var user = User.CreateFromExternalIdentity(null, null, new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero));
        factory.Users.Users.Add(user);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.IssueAccessToken(user.Id));

        return (client, user.Id);
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
