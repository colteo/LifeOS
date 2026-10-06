using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LifeOS.Contracts.Notifications;

namespace LifeOS.IntegrationTests.Http;

// AUTO-003A: GET/PUT /api/notification-preferences through the real API pipeline (routing, JWT,
// authorization) with the in-memory preferences repository.
public class NotificationPreferencesHttpTests
{
    private const string Path = "/api/notification-preferences";
    private static readonly Guid UserA = Guid.CreateVersion7();
    private static readonly Guid UserB = Guid.CreateVersion7();

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task RequiresAUserAccessToken(string method)
    {
        await using var factory = new LifeOSApiFactory();

        var response = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), Path)
        {
            Content = method == "PUT" ? JsonContent.Create(Request(false, false, "22:00", "08:00")) : null
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.NotificationPreferences.Saved);
    }

    [Fact]
    public async Task Get_WithoutSavedPreferences_IsTheDefaults()
    {
        await using var factory = new LifeOSApiFactory();

        using var body = JsonDocument.Parse(await Client(factory, UserA).GetStringAsync(Path));

        Assert.Equal(
            ["recurringTransactionReminders", "plannedExpenseReminders", "quietHoursStart", "quietHoursEnd"],
            body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(new NotificationPreferencesResponse(true, true, "22:00", "08:00"),
            body.RootElement.Deserialize<NotificationPreferencesResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task Put_ThenGet_RoundTrips_AndIsPerUser()
    {
        await using var factory = new LifeOSApiFactory();
        var clientA = Client(factory, UserA);

        var put = await clientA.PutAsJsonAsync(Path, Request(false, true, "23:15", "07:30"));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(new NotificationPreferencesResponse(false, true, "23:15", "07:30"), await clientA.GetFromJsonAsync<NotificationPreferencesResponse>(Path));
        Assert.Equal(new NotificationPreferencesResponse(true, true, "22:00", "08:00"), await Client(factory, UserB).GetFromJsonAsync<NotificationPreferencesResponse>(Path));
        Assert.Equal([UserA], factory.NotificationPreferences.Saved.Keys);
    }

    [Fact]
    public async Task Put_CannotTargetAnotherUser()
    {
        await using var factory = new LifeOSApiFactory();

        // An extra userId in the body is ignored: the owner is the token's user.
        var response = await Client(factory, UserA).PutAsync(Path, new StringContent(
            $$"""{"userId":"{{UserB}}","recurringTransactionReminders":false,"plannedExpenseReminders":false,"quietHoursStart":"21:00","quietHoursEnd":"09:00"}""",
            Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal([UserA], factory.NotificationPreferences.Saved.Keys);
    }

    [Theory]
    [InlineData("""{"plannedExpenseReminders":true,"quietHoursStart":"22:00","quietHoursEnd":"08:00"}""", "recurringTransactionReminders")]
    [InlineData("""{"recurringTransactionReminders":true,"quietHoursStart":"22:00","quietHoursEnd":"08:00"}""", "plannedExpenseReminders")]
    [InlineData("""{"recurringTransactionReminders":true,"plannedExpenseReminders":true,"quietHoursEnd":"08:00"}""", "quietHoursStart")]
    [InlineData("""{"recurringTransactionReminders":true,"plannedExpenseReminders":true,"quietHoursStart":"25:00","quietHoursEnd":"08:00"}""", "quietHoursStart")]
    [InlineData("""{"recurringTransactionReminders":true,"plannedExpenseReminders":true,"quietHoursStart":"22:00","quietHoursEnd":"8am"}""", "quietHoursEnd")]
    [InlineData("""{"recurringTransactionReminders":true,"plannedExpenseReminders":true,"quietHoursStart":"22:00","quietHoursEnd":"08:00:30"}""", "quietHoursEnd")]
    [InlineData("""{"recurringTransactionReminders":true,"plannedExpenseReminders":true,"quietHoursStart":"22:00","quietHoursEnd":"22:00"}""", "quietHoursEnd")]
    public async Task Put_ValidatesEveryField(string json, string field)
    {
        await using var factory = new LifeOSApiFactory();

        var response = await Client(factory, UserA).PutAsync(Path, new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), field);
        Assert.Empty(factory.NotificationPreferences.Saved);
    }

    [Fact]
    public async Task Put_ForAMissingUser_Is404()
    {
        await using var factory = new LifeOSApiFactory();
        factory.NotificationPreferences.ExistingUsers = [];

        var response = await Client(factory, UserA).PutAsJsonAsync(Path, Request(true, true, "22:00", "08:00"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static SetNotificationPreferencesRequest Request(bool recurring, bool planned, string start, string end) => new(recurring, planned, start, end);

    private static HttpClient Client(LifeOSApiFactory factory, Guid userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.IssueAccessToken(userId));

        return client;
    }
}
