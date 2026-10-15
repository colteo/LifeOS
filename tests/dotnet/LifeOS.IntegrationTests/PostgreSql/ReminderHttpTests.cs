using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Application.Notifications;
using LifeOS.Application.Reminders;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Reminders;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LifeOS.IntegrationTests.PostgreSql;

// REM-001 §9 through the real API pipeline (JWT, routing, endpoints) and real PostgreSQL. The reminder
// handlers' clock is frozen at Wednesday 14 Oct 2026 09:00 in Rome (+120); authentication keeps the
// real clock.
[Collection(PostgreSqlCollection.Name)]
public class ReminderHttpTests(PostgreSqlFixture fixture)
{
    private const string Reminders = "/api/reminders";
    private const int Rome = 120;
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 7, 0, 0, TimeSpan.Zero);

    private static readonly ReminderScheduleRequest Daily0830 = new("Daily", null, null, "08:30");
    private static readonly ReminderScheduleRequest Dentist = new("Once", new DateOnly(2026, 10, 20), null, "10:00");

    [Fact]
    public async Task EveryEndpoint_RequiresAuthentication()
    {
        await using var factory = await FactoryAsync();
        var anonymous = factory.CreateClient();
        var path = $"{Reminders}/{Guid.CreateVersion7()}";

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Reminders)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Reminders, Create("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(path, new UpdateReminderRequest("x", null, Daily0830, Rome))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"{path}/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{path}/resume", new ResumeReminderRequest(Rome))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{path}/snooze", new SnoozeReminderRequest(15))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(path)).StatusCode);
    }

    [Fact]
    public async Task CreateReadAndList_RoundTripThroughPostgreSql()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        var created = await client.PostAsJsonAsync(Reminders,
            new CreateReminderRequest("Custom", "  Call the dentist ", " Ask about Friday ", Dentist, Rome));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var reminder = (await created.Content.ReadFromJsonAsync<ReminderResponse>())!;
        Assert.Equal($"{Reminders}/{reminder.Id}", created.Headers.Location!.ToString());
        Assert.Equal(("Custom", "Call the dentist", "Call the dentist", "Ask about Friday", "Active", "2026-10-20T10:00"),
            (reminder.Kind, reminder.Label, reminder.Title, reminder.Note, reminder.Status, reminder.NextDueLocal));
        Assert.Equal(new ReminderScheduleResponse("Once", new DateOnly(2026, 10, 20), null, "10:00"), reminder.Schedule);
        Assert.False(reminder.InsideQuietHours);

        var weekly = (await (await client.PostAsJsonAsync(Reminders, new CreateReminderRequest(null, "Water the plants", null,
            new ReminderScheduleRequest("Weekly", null, ["thursday", "Monday"], "19:00"), Rome))).Content.ReadFromJsonAsync<ReminderResponse>())!;
        Assert.Equal(["Monday", "Thursday"], weekly.Schedule.Weekdays!);
        Assert.Equal("2026-10-15T19:00", weekly.NextDueLocal);

        var late = (await (await client.PostAsJsonAsync(Reminders, Create("Late", new ReminderScheduleRequest("Daily", null, null, "23:00"))))
            .Content.ReadFromJsonAsync<ReminderResponse>())!;
        Assert.True(late.InsideQuietHours);

        Assert.Equal(reminder, await client.GetFromJsonAsync<ReminderResponse>($"{Reminders}/{reminder.Id}"));

        var list = (await client.GetFromJsonAsync<RemindersResponse>(Reminders))!;
        Assert.Equal(["Late", "Water the plants", "Call the dentist"], list.Upcoming.Select(item => item.Label));
        Assert.Empty(list.Paused);
        Assert.Empty(list.Completed);
        // A fresh dev user has no zone yet; FCM is not configured in tests.
        Assert.Equal((false, false), (list.TimeZoneKnown, list.PushAvailable));
    }

    [Fact]
    public async Task Json_UsesStringsForEnumLikeValues()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var response = await client.PostAsJsonAsync(Reminders, Create("Vitamins"));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal("Custom", root.GetProperty("kind").GetString());
        Assert.Equal("Active", root.GetProperty("status").GetString());
        Assert.Equal("Daily", root.GetProperty("schedule").GetProperty("kind").GetString());
        Assert.Equal("08:30", root.GetProperty("schedule").GetProperty("time").GetString());
        Assert.Equal("2026-10-15T08:30", root.GetProperty("nextDueLocal").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastOutcome").ValueKind);
    }

    [Fact]
    public async Task InvalidInput_Is400_WithTheField()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        await AssertInvalidAsync(client.PostAsJsonAsync(Reminders, Create(" ")), "title");
        await AssertInvalidAsync(client.PostAsJsonAsync(Reminders, Create("x") with { Kind = "HabitIncomplete" }), "kind");
        await AssertInvalidAsync(client.PostAsJsonAsync(Reminders, Create("x") with { UtcOffsetMinutes = null }), "utcOffsetMinutes");
        await AssertInvalidAsync(client.PostAsJsonAsync(Reminders, Create("x") with { UtcOffsetMinutes = 900 }), "utcOffsetMinutes");
        await AssertInvalidAsync(client.PostAsJsonAsync(Reminders, Create("x") with { Schedule = null }), "schedule");
        await AssertInvalidAsync(client.PostAsJsonAsync(Reminders, Create("x", new ReminderScheduleRequest("Hourly", null, null, "08:30"))), "schedule.kind");
        await AssertInvalidAsync(client.PostAsJsonAsync(Reminders, Create("x", new ReminderScheduleRequest("1", null, null, "08:30"))), "schedule.kind");
        await AssertInvalidAsync(client.PostAsJsonAsync(Reminders, Create("x", new ReminderScheduleRequest("Daily", null, null, "8:30pm"))), "schedule.time");
        await AssertInvalidAsync(client.PostAsJsonAsync(Reminders, Create("x", new ReminderScheduleRequest("Weekly", null, [], "08:30"))), "schedule.weekdays");
        await AssertInvalidAsync(client.PostAsJsonAsync(Reminders, Create("x", new ReminderScheduleRequest("Weekly", null, ["Funday"], "08:30"))), "schedule.weekdays");
        await AssertInvalidAsync(client.PostAsJsonAsync(Reminders, Create("x", new ReminderScheduleRequest("Once", null, null, "08:30"))), "schedule.date");
        await AssertInvalidAsync(client.PostAsJsonAsync(Reminders, Create("x", new ReminderScheduleRequest("Once", new DateOnly(2026, 10, 14), null, "08:59"))),
            "schedule.date");

        var reminder = await CreateAsync(client, "Vitamins");
        await AssertInvalidAsync(client.PostAsJsonAsync($"{Reminders}/{reminder.Id}/snooze", new SnoozeReminderRequest(30)), "minutes");
        await AssertInvalidAsync(client.PostAsJsonAsync($"{Reminders}/{reminder.Id}/snooze", new SnoozeReminderRequest(null)), "minutes");
        await AssertInvalidAsync(client.PostAsJsonAsync($"{Reminders}/{reminder.Id}/resume", new ResumeReminderRequest(null)), "utcOffsetMinutes");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{Reminders}/{reminder.Id}", new UpdateReminderRequest("x", new string('n', 501), Daily0830, Rome)),
            "note");
    }

    [Fact]
    public async Task EditPauseResumeSnoozeDelete_WithConflictCodes()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var reminder = await CreateAsync(client, "Vitamins");
        var path = $"{Reminders}/{reminder.Id}";

        var edited = await ReadAsync(await client.PutAsJsonAsync(path,
            new UpdateReminderRequest("Vitamin D", "With food", new ReminderScheduleRequest("Daily", null, null, "21:00"), Rome)));
        Assert.Equal(("Vitamin D", "With food", "2026-10-14T21:00"), (edited.Title, edited.Note, edited.NextDueLocal));

        var snoozed = await ReadAsync(await client.PostAsJsonAsync($"{path}/snooze", new SnoozeReminderRequest(60)));
        Assert.Equal(Now.AddHours(1), snoozed.SnoozeUntilUtc);

        var paused = await ReadAsync(await client.PostAsync($"{path}/pause", null));
        Assert.Equal(("Paused", (DateTimeOffset?)null), (paused.Status, paused.SnoozeUntilUtc));
        Assert.Equal("Paused", (await ReadAsync(await client.PostAsync($"{path}/pause", null))).Status);
        await AssertConflictAsync(client.PostAsJsonAsync($"{path}/snooze", new SnoozeReminderRequest(15)), "reminder_paused");

        var list = (await client.GetFromJsonAsync<RemindersResponse>(Reminders))!;
        Assert.Equal((0, 1), (list.Upcoming.Count, list.Paused.Count));

        var resumed = await ReadAsync(await client.PostAsJsonAsync($"{path}/resume", new ResumeReminderRequest(Rome)));
        Assert.Equal(("Active", "2026-10-14T21:00"), (resumed.Status, resumed.NextDueLocal));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task ACompletedReminder_CannotBeResumedOrPaused()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var reminder = await CreateAsync(client, "Dentist", Dentist);

        await using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
            var stored = await db.Reminders.SingleAsync(row => row.Id == reminder.Id);
            stored.Advance(new DateTime(2026, 10, 20, 10, 0, 0), LifeOS.Domain.Reminders.ReminderOutcome.Notified, Now);
            await db.SaveChangesAsync();
        }

        var path = $"{Reminders}/{reminder.Id}";
        await AssertConflictAsync(client.PostAsJsonAsync($"{path}/resume", new ResumeReminderRequest(Rome)), "reminder_completed");
        await AssertConflictAsync(client.PostAsync($"{path}/pause", null), "reminder_completed");

        var list = (await client.GetFromJsonAsync<RemindersResponse>(Reminders))!;
        var completed = Assert.Single(list.Completed);
        Assert.Equal(("Completed", (string?)null, "Notified"), (completed.Status, completed.NextDueLocal, completed.LastOutcome));

        // Snooze is still allowed (REM-US7: tap → snooze after the one-time reminder fired).
        Assert.Equal(Now.AddMinutes(15), (await ReadAsync(await client.PostAsJsonAsync($"{path}/snooze", new SnoozeReminderRequest(15)))).SnoozeUntilUtc);
    }

    [Fact]
    public async Task AnotherUsersReminder_Is404_LikeAMissingOne_AndStaysUntouched()
    {
        await using var factory = await FactoryAsync();
        var owner = await SignInAsync(factory);
        var other = await SignInAsync(factory);
        var reminder = await CreateAsync(owner, "Private");
        var path = $"{Reminders}/{reminder.Id}";
        var missing = $"{Reminders}/{Guid.CreateVersion7()}";

        Assert.Empty((await other.GetFromJsonAsync<RemindersResponse>(Reminders))!.Upcoming);

        var foreignGet = await other.GetAsync(path);
        var missingGet = await owner.GetAsync(missing);
        Assert.Equal(HttpStatusCode.NotFound, foreignGet.StatusCode);
        Assert.Equal(await ProblemTextAsync(missingGet), await ProblemTextAsync(foreignGet));

        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(path, new UpdateReminderRequest("Stolen", null, Daily0830, Rome))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"{path}/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{path}/resume", new ResumeReminderRequest(Rome))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{path}/snooze", new SnoozeReminderRequest(15))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(path)).StatusCode);

        var stored = (await owner.GetFromJsonAsync<ReminderResponse>(path))!;
        Assert.Equal(("Private", "Active", (DateTimeOffset?)null), (stored.Title, stored.Status, stored.SnoozeUntilUtc));
    }

    [Fact]
    public async Task TheLimit_IsAConflict()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        for (var index = 0; index < ReminderLimits.MaxOpenReminders; index++)
        {
            await CreateAsync(client, $"Reminder {index}");
        }

        await AssertConflictAsync(client.PostAsJsonAsync(Reminders, Create("One too many")), "reminder_limit_reached");
    }

    // R4: the app's client against the real API and PostgreSQL.
    [Fact]
    public async Task ApiClient_RoundTrip()
    {
        await using var factory = await FactoryAsync();
        var api = new LifeOS.App.Services.Reminders.RemindersApiClient(await SignInAsync(factory), () => Rome);

        var created = await api.CreateAsync(Create("Vitamins"));
        Assert.True(created.IsSuccess, string.Join(" ", created.Errors));
        var id = created.Value!.Id;

        Assert.Equal("Vitamins", (await api.GetAsync(id)).Value!.Title);
        Assert.Single((await api.GetAllAsync()).Value!.Upcoming);
        Assert.Equal(Now.AddMinutes(15), (await api.SnoozeAsync(id, 15)).Value!.SnoozeUntilUtc);
        Assert.Equal("Paused", (await api.PauseAsync(id)).Value!.Status);
        Assert.Equal(["Resume the reminder before snoozing it."], (await api.SnoozeAsync(id, 15)).Errors);
        Assert.Equal("Active", (await api.ResumeAsync(id)).Value!.Status);
        Assert.Equal("2026-10-14T21:00", (await api.UpdateAsync(id, new UpdateReminderRequest("Vitamins", null,
            new ReminderScheduleRequest("Daily", null, null, "21:00"), Rome))).Value!.NextDueLocal);

        Assert.True((await api.DeleteAsync(id)).IsSuccess);
        Assert.True(LifeOS.App.Services.Reminders.RemindersApiClient.IsNotFound(await api.GetAsync(id)));
        Assert.True((await api.DeleteAsync(id)).IsSuccess);
    }

    // ---- Helpers ----

    private static CreateReminderRequest Create(string title, ReminderScheduleRequest? schedule = null) =>
        new("Custom", title, null, schedule ?? Daily0830, Rome);

    private static async Task<ReminderResponse> CreateAsync(HttpClient client, string title, ReminderScheduleRequest? schedule = null)
    {
        var response = await client.PostAsJsonAsync(Reminders, Create(title, schedule));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<ReminderResponse>())!;
    }

    private static async Task<ReminderResponse> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<ReminderResponse>())!;
    }

    private static async Task<(string?, string?, int)> ProblemTextAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = problem.RootElement;

        return (root.GetProperty("title").GetString(), root.GetProperty("detail").GetString(), root.GetProperty("status").GetInt32());
    }

    private static async Task AssertInvalidAsync(Task<HttpResponseMessage> request, string field)
    {
        using var response = await request;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{field}: {(int)response.StatusCode} {body}");

        Assert.DoesNotContain("Exception", body);
        using var problem = JsonDocument.Parse(body);
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), body);
    }

    private static async Task AssertConflictAsync(Task<HttpResponseMessage> request, string code)
    {
        using var response = await request;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private async Task<ReminderApiFactory> FactoryAsync()
    {
        await using var scope = fixture.CreateScope();

        return new ReminderApiFactory(scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!);
    }

    private static async Task<HttpClient> SignInAsync(ReminderApiFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest("reminder-test-" + Guid.NewGuid(), null, null));
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);

        return client;
    }

    private sealed class ReminderApiFactory(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                // Freeze the reminders' "now" without changing JWT issuance/validation clocks.
                var clock = new FixedTimeProvider(Now);
                Replace(services, provider => new GetRemindersHandler(Repository(provider), Preferences(provider), clock, provider.GetService<IPushNotificationSender>()));
                Replace(services, provider => new CreateReminderHandler(Repository(provider), Preferences(provider), clock));
                Replace(services, provider => new UpdateReminderHandler(Repository(provider), Preferences(provider), clock));
                Replace(services, provider => new PauseReminderHandler(Repository(provider), Preferences(provider), clock));
                Replace(services, provider => new SnoozeReminderHandler(Repository(provider), Preferences(provider), clock));
            });
            builder.UseSetting("ConnectionStrings:PostgreSQL", connection);
            builder.UseSetting("Authentication:LifeOS:SigningKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting(LifeOS.Api.Authentication.DevelopmentSignIn.EnabledKey, "true");
            builder.UseSetting("Authentication:Google:ClientId", "test-client-id.apps.googleusercontent.com");
            builder.UseSetting("Authentication:Google:ClientSecret", "test-client-secret");
            builder.UseSetting("Authentication:Google:AllowedEmails:0", "person@example.com");
        }

        private static IReminderRepository Repository(IServiceProvider provider) => provider.GetRequiredService<IReminderRepository>();

        private static INotificationPreferencesRepository Preferences(IServiceProvider provider) =>
            provider.GetRequiredService<INotificationPreferencesRepository>();

        private static void Replace<T>(IServiceCollection services, Func<IServiceProvider, T> factory) where T : class
        {
            services.RemoveAll<T>();
            services.AddScoped(factory);
        }
    }
}
