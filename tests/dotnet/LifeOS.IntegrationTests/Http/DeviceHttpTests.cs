using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LifeOS.Contracts.Devices;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.Users;

namespace LifeOS.IntegrationTests.Http;

// AUTO-001 WP3A: PUT/DELETE /api/devices/{installationId} for the signed-in user.
public class DeviceHttpTests
{
    private const string Installation = "5f0c1d2e-3a4b-4c5d-8e9f-0a1b2c3d4e5f";
    private const string Path = "/api/devices/" + Installation;

    [Fact]
    public async Task Anonymous_Returns401()
    {
        await using var factory = new LifeOSApiFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(Path, Permitted("token"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync(Path)).StatusCode);
        Assert.Empty(factory.Devices.Rows);
    }

    [Fact]
    public async Task Register_Returns204_AndIsIdempotent()
    {
        await using var factory = new LifeOSApiFactory();
        var (client, userId) = SignedIn(factory);

        var first = await client.PutAsJsonAsync(Path, Permitted("token-1"));
        var second = await client.PutAsJsonAsync(Path, Permitted("token-1"));

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsStringAsync());
        var row = Assert.Single(factory.Devices.Rows);
        Assert.Equal((userId, DeviceRegistrationStatus.Active, "token-1", PushProvider.Fcm), (row.UserId, row.Status, row.PushToken, row.PushProvider));
    }

    [Fact]
    public async Task Register_UpdatesTheToken_AndPermission()
    {
        await using var factory = new LifeOSApiFactory();
        var (client, userId) = SignedIn(factory);

        await client.PutAsJsonAsync(Path, Permitted("token-1"));
        await client.PutAsJsonAsync(Path, Permitted("token-2"));
        Assert.Equal("token-2", factory.Devices.Single(userId, Installation).PushToken);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(Path, new RegisterDeviceRequest("Android", null, false))).StatusCode);
        var row = factory.Devices.Single(userId, Installation);
        Assert.Equal((DeviceRegistrationStatus.Inactive, DeviceInactiveReason.PermissionDenied), (row.Status, row.InactiveReason!.Value));
        Assert.Null(row.PushToken);
        Assert.Single(factory.Devices.Rows);
    }

    [Fact]
    public async Task AnotherSignedInUser_TakesTheInstallationOver_WithItsOwnRow()
    {
        await using var factory = new LifeOSApiFactory();
        var (clientA, userA) = SignedIn(factory);
        var (clientB, userB) = SignedIn(factory);

        await clientA.PutAsJsonAsync(Path, Permitted("token-a"));
        await clientA.DeleteAsync(Path);
        Assert.Equal(HttpStatusCode.NoContent, (await clientB.PutAsJsonAsync(Path, Permitted("token-b"))).StatusCode);

        Assert.Equal(DeviceRegistrationStatus.Inactive, factory.Devices.Single(userA, Installation).Status);
        Assert.Equal((DeviceRegistrationStatus.Active, "token-b"), (factory.Devices.Single(userB, Installation).Status, factory.Devices.Single(userB, Installation).PushToken));
        Assert.Single(factory.Devices.Rows, row => row.Status == DeviceRegistrationStatus.Active);

        // A no longer owns an active registration there: A's DELETE only touches A's own row.
        Assert.Equal(HttpStatusCode.NoContent, (await clientA.DeleteAsync(Path)).StatusCode);
        Assert.Equal(DeviceRegistrationStatus.Active, factory.Devices.Single(userB, Installation).Status);
    }

    [Fact]
    public async Task Delete_SignsOut_Idempotently_AndIs404ForAnInstallationTheCallerHasNoRowFor()
    {
        await using var factory = new LifeOSApiFactory();
        var (owner, ownerId) = SignedIn(factory);
        var (other, _) = SignedIn(factory);
        await owner.PutAsJsonAsync(Path, Permitted("token"));

        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(Path)).StatusCode);
        Assert.Equal(DeviceRegistrationStatus.Active, factory.Devices.Single(ownerId, Installation).Status);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(Path)).StatusCode);

        var row = factory.Devices.Single(ownerId, Installation);
        Assert.Equal((DeviceRegistrationStatus.Inactive, DeviceInactiveReason.SignedOut), (row.Status, row.InactiveReason!.Value));
        Assert.Null(row.PushToken);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.DeleteAsync("/api/devices/never-registered-0001")).StatusCode);
    }

    [Theory]
    [InlineData("/api/devices/too-short")]
    [InlineData("/api/devices/has.dots.in.it.000000")]
    [InlineData("/api/devices/" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task InvalidInstallationId_Returns400(string path)
    {
        await using var factory = new LifeOSApiFactory();
        var (client, _) = SignedIn(factory);

        var put = await client.PutAsJsonAsync(path, Permitted("token"));
        var delete = await client.DeleteAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("installationId", await put.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
        Assert.Empty(factory.Devices.Rows);
    }

    [Theory]
    [InlineData("""{"platform":"Android","notificationsPermitted":true}""", "pushToken")]
    [InlineData("""{"platform":"Android","pushToken":"","notificationsPermitted":true}""", "pushToken")]
    [InlineData("""{"platform":"Android","pushToken":"has space","notificationsPermitted":true}""", "pushToken")]
    [InlineData("""{"platform":"Android","pushToken":"token","notificationsPermitted":false}""", "pushToken")]
    [InlineData("""{"platform":"iOS","pushToken":"token","notificationsPermitted":true}""", "platform")]
    [InlineData("""{"platform":"Android","pushToken":"token"}""", "notificationsPermitted")]
    public async Task InvalidBody_Returns400(string json, string field)
    {
        await using var factory = new LifeOSApiFactory();
        var (client, _) = SignedIn(factory);

        var response = await client.PutAsync(Path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.Devices.Rows);
    }

    [Fact]
    public async Task TooLongToken_Returns400()
    {
        await using var factory = new LifeOSApiFactory();
        var (client, _) = SignedIn(factory);

        var response = await client.PutAsJsonAsync(Path, Permitted(new string('a', 4097)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The owner is the access token's user: a userId in the body or query never chooses ownership.
    [Fact]
    public async Task UserIdInBodyOrQuery_IsIgnored()
    {
        await using var factory = new LifeOSApiFactory();
        var (client, userId) = SignedIn(factory);
        var victim = Guid.CreateVersion7();
        var json = $$"""{"platform":"Android","pushToken":"token","notificationsPermitted":true,"userId":"{{victim}}"}""";

        var response = await client.PutAsync(Path + "?userId=" + victim, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(userId, Assert.Single(factory.Devices.Rows).UserId);
    }

    [Fact]
    public async Task MissingUser_Returns404()
    {
        await using var factory = new LifeOSApiFactory();
        factory.Devices.ExistingUsers = [];
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.IssueAccessToken(Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(Path, Permitted("token"))).StatusCode);
    }

    private static RegisterDeviceRequest Permitted(string token) => new("Android", token, true);

    private static (HttpClient Client, Guid UserId) SignedIn(LifeOSApiFactory factory)
    {
        var client = factory.CreateClient();
        var user = User.CreateFromExternalIdentity(null, null, new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero));
        factory.Users.Users.Add(user);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.IssueAccessToken(user.Id));

        return (client, user.Id);
    }
}
