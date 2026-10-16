using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Application.Habits;
using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Contracts.Auth;
using LifeOS.Contracts.Habits;
using LifeOS.Contracts.Nutrition.Hydration;
using LifeOS.Infrastructure.Persistence;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LifeOS.IntegrationTests.PostgreSql;

// HAB-001 §8 through the real API pipeline (JWT, routing, endpoints) and real PostgreSQL. The habit
// handlers' clock is frozen at Wednesday 14 Oct 2026 10:00 in Rome (+120); authentication keeps the
// real clock.
[Collection(PostgreSqlCollection.Name)]
public class HabitHttpTests(PostgreSqlFixture fixture)
{
    private const string Habits = "/api/habits";
    private const int Rome = 120;
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 14);

    private static readonly HabitFrequencyRequest Daily = new("Daily", null, null);

    [Fact]
    public async Task EveryEndpoint_RequiresAuthentication()
    {
        await using var factory = await FactoryAsync();
        var anonymous = factory.CreateClient();
        var path = $"{Habits}/{Guid.CreateVersion7()}";

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Habits)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Habits}/archived")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{path}/history?from=2026-10-01&to=2026-10-14")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Habits, Create("Read"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(path, new UpdateHabitRequest("Read", null, null, Rome))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{path}/archive", new HabitTodayRequest(Rome))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"{path}/restore", new HabitTodayRequest(Rome))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PutAsJsonAsync($"{path}/check-ins/2026-10-14", new SetHabitCheckInRequest("Done", null, Rome))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"{path}/check-ins/2026-10-14?utcOffsetMinutes={Rome}")).StatusCode);
    }

    [Fact]
    public async Task CreateCheckInAndReopen_RoundTripsThroughPostgreSql()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);

        var created = await client.PostAsJsonAsync(Habits, new CreateHabitRequest("  Read 20 pages ", null, "Manual",
            new HabitFrequencyRequest("Weekdays", ["Monday", "wednesday", "Friday"], null), Today.AddDays(-9), Rome));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var habit = (await created.Content.ReadFromJsonAsync<HabitDetailResponse>())!;
        Assert.Equal($"{Habits}/{habit.Id}", created.Headers.Location!.ToString());
        Assert.Equal(("Read 20 pages", "Manual", "Active", Today.AddDays(-9)), (habit.Name, habit.CompletionSource, habit.Status, habit.StartsOn));
        Assert.Equal(new HabitFrequencyResponse("Weekdays", ["Monday", "Wednesday", "Friday"], null, Today.AddDays(-9)) with { Weekdays = null },
            habit.Frequency with { Weekdays = null });
        Assert.Equal(["Monday", "Wednesday", "Friday"], habit.Frequency.Weekdays!);
        Assert.Equal(("Pending", 0, "Day"), (habit.Progress.TodayStatus, habit.Progress.CurrentStreak, habit.Progress.StreakUnit));

        // One tap: Done today, saved through the API.
        var done = await client.PutAsJsonAsync($"{Habits}/{habit.Id}/check-ins/2026-10-14", new SetHabitCheckInRequest("Done", null, Rome));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var day = (await done.Content.ReadFromJsonAsync<HabitDayResponse>())!;
        Assert.Equal((habit.Id, Today, "Done", 1), (day.HabitId, day.Date, day.Status, day.Progress.CurrentStreak));

        // Backfill Monday, excuse last Friday with a note.
        await client.PutAsJsonAsync($"{Habits}/{habit.Id}/check-ins/2026-10-12", new SetHabitCheckInRequest("Done", null, Rome));
        var excused = (await (await client.PutAsJsonAsync($"{Habits}/{habit.Id}/check-ins/2026-10-09",
            new SetHabitCheckInRequest("Excused", "  Travel day ", Rome))).Content.ReadFromJsonAsync<HabitDayResponse>())!;
        Assert.Equal(("Excused", "Travel day"), (excused.Status, excused.Note));

        // Reopening: the list and the detail are read back from PostgreSQL.
        var list = (await client.GetFromJsonAsync<HabitsForDayResponse>($"{Habits}?date=2026-10-14&utcOffsetMinutes={Rome}"))!;
        var item = Assert.Single(list.Habits);
        // Wed 14 and Mon 12 Done; Fri 9 excused (neutral); Wed 7 missed ends the streak.
        Assert.Equal((habit.Id, "Done", "Done", 2, "Day"), (item.Id, item.Status, item.Group, item.CurrentStreak, item.StreakUnit));
        Assert.Equal((Today, Today, 0, 0), (list.Date, list.Today, list.UpcomingCount, list.ArchivedCount));

        var detail = (await client.GetFromJsonAsync<HabitDetailResponse>($"{Habits}/{habit.Id}?utcOffsetMinutes={Rome}&window=30"))!;
        Assert.Equal(("Done", 2, 2), (detail.Progress.TodayStatus, detail.Progress.CurrentStreak, detail.Progress.BestStreak));
        // Mon 5 and Wed 7 Oct are Missed; Mon 12 and Wed 14 Done; Fri 9 excused.
        Assert.Equal(new HabitAdherenceResponse(2, 4, 50, 30), detail.Progress.Adherence);
        Assert.Equal(new DateOnly(2026, 9, 14), detail.RecentDays[0].Date);
        Assert.Equal(new HabitDayItemResponse(Today.AddDays(-5), "Excused", "Travel day"), detail.RecentDays.Single(d => d.Date == Today.AddDays(-5)));

        // Undo: back to Pending; again is still 200.
        var undo = await client.DeleteAsync($"{Habits}/{habit.Id}/check-ins/2026-10-14?utcOffsetMinutes={Rome}");
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal("Pending", (await undo.Content.ReadFromJsonAsync<HabitDayResponse>())!.Status);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"{Habits}/{habit.Id}/check-ins/2026-10-14?utcOffsetMinutes={Rome}")).StatusCode);

        var history = (await client.GetFromJsonAsync<HabitHistoryResponse>(
            $"{Habits}/{habit.Id}/history?from=2026-10-04&to=2026-10-14&utcOffsetMinutes={Rome}"))!;
        Assert.Equal(
            ["Inactive", "Missed", "NotScheduled", "Missed", "NotScheduled", "Excused", "NotScheduled", "NotScheduled", "Done", "NotScheduled", "Pending"],
            history.Days.Select(d => d.Status));
    }

    [Fact]
    public async Task Json_UsesStringsForEnumLikeValues()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var response = await client.PostAsJsonAsync(Habits, new CreateHabitRequest("Gym", null, null, new HabitFrequencyRequest("TimesPerWeek", null, 3), null, Rome));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal("Manual", root.GetProperty("completionSource").GetString());
        Assert.Equal("TimesPerWeek", root.GetProperty("frequency").GetProperty("kind").GetString());
        Assert.Equal(3, root.GetProperty("frequency").GetProperty("timesPerWeek").GetInt32());
        Assert.Equal("2026-10-14", root.GetProperty("startsOn").GetString());
        Assert.Equal("Week", root.GetProperty("progress").GetProperty("streakUnit").GetString());
        Assert.Equal(0, root.GetProperty("progress").GetProperty("thisWeek").GetProperty("done").GetInt32());
        Assert.Equal(3, root.GetProperty("progress").GetProperty("thisWeek").GetProperty("target").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("progress").GetProperty("adherence").GetProperty("percent").ValueKind);

        var list = await client.GetStringAsync($"{Habits}?date=2026-10-14&utcOffsetMinutes={Rome}");
        Assert.Contains("\"group\":\"ToDo\"", list);
        Assert.Contains("\"status\":\"Pending\"", list);
    }

    [Fact]
    public async Task InvalidInput_Is400_WithTheField()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var habit = await CreateAsync(client, "Read", startsOn: Today.AddDays(-3));
        var checkIn = $"{Habits}/{habit.Id}/check-ins";

        await AssertInvalidAsync(client.PostAsJsonAsync(Habits, Create(" ")), "name");
        await AssertInvalidAsync(client.PostAsJsonAsync(Habits, Create("Read") with { UtcOffsetMinutes = null }), "utcOffsetMinutes");
        await AssertInvalidAsync(client.PostAsJsonAsync(Habits, Create("Read") with { UtcOffsetMinutes = 900 }), "utcOffsetMinutes");
        await AssertInvalidAsync(client.PostAsJsonAsync(Habits, Create("Read") with { Frequency = null }), "frequency");
        await AssertInvalidAsync(client.PostAsJsonAsync(Habits, Create("Read") with { Frequency = new("Monthly", null, null) }), "frequency.kind");
        await AssertInvalidAsync(client.PostAsJsonAsync(Habits, Create("Read") with { Frequency = new("Inactive", null, null) }), "frequency.kind");
        await AssertInvalidAsync(client.PostAsJsonAsync(Habits, Create("Read") with { Frequency = new("Weekdays", ["1"], null) }), "frequency.weekdays");
        await AssertInvalidAsync(client.PostAsJsonAsync(Habits, Create("Read") with { Frequency = new("Weekdays", [], null) }), "frequency.weekdays");
        await AssertInvalidAsync(client.PostAsJsonAsync(Habits, Create("Read") with { Frequency = new("TimesPerWeek", null, 8) }), "frequency.timesPerWeek");
        await AssertInvalidAsync(client.PostAsJsonAsync(Habits, Create("Read") with { StartsOn = Today.AddDays(366) }), "startsOn");
        await AssertInvalidAsync(client.PostAsJsonAsync(Habits, Create("Read") with { CompletionSource = "Water" }), "completionSource");

        await AssertInvalidAsync(client.PutAsJsonAsync($"{checkIn}/2026-10-15", new SetHabitCheckInRequest("Done", null, Rome)), "date");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{checkIn}/2026-10-10", new SetHabitCheckInRequest("Done", null, Rome)), "date");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{checkIn}/2026-10-14", new SetHabitCheckInRequest("Missed", null, Rome)), "status");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{checkIn}/2026-10-14", new SetHabitCheckInRequest("0", null, Rome)), "status");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{checkIn}/2026-10-14", new SetHabitCheckInRequest("Done", null, null)), "utcOffsetMinutes");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{checkIn}/2026-10-14", new SetHabitCheckInRequest("Excused", new string('n', 281), Rome)), "note");
        await AssertInvalidAsync(client.DeleteAsync($"{checkIn}/2026-10-14"), "utcOffsetMinutes");

        await AssertInvalidAsync(client.GetAsync($"{Habits}?date=2026-10-15&utcOffsetMinutes={Rome}"), "date");
        await AssertInvalidAsync(client.GetAsync($"{Habits}/{habit.Id}?utcOffsetMinutes={Rome}&window=14"), "window");
        await AssertInvalidAsync(client.GetAsync($"{Habits}/{habit.Id}/history?from=2025-10-13&to=2026-10-14&utcOffsetMinutes={Rome}"), "from");
        await AssertInvalidAsync(client.GetAsync($"{Habits}/{habit.Id}/history?to=2026-10-14&utcOffsetMinutes={Rome}"), "from");
        await AssertInvalidAsync(client.PutAsJsonAsync($"{Habits}/{habit.Id}", new UpdateHabitRequest("", null, null, Rome)), "name");
        await AssertInvalidAsync(client.PostAsJsonAsync($"{Habits}/{habit.Id}/archive", new HabitTodayRequest(null)), "utcOffsetMinutes");

        // In UTC (offset 0) it is still the 14th at 08:00: the same date is not in the future there.
        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsJsonAsync($"{checkIn}/2026-10-14", new SetHabitCheckInRequest("Done", null, 0))).StatusCode);

        Assert.Single((await client.GetFromJsonAsync<HabitsForDayResponse>($"{Habits}?date=2026-10-14&utcOffsetMinutes={Rome}"))!.Habits);
    }

    [Fact]
    public async Task EditArchiveRestoreDelete_WithConflictCodes()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var habit = await CreateAsync(client, "Read", startsOn: Today.AddDays(-7));
        var path = $"{Habits}/{habit.Id}";

        var edited = (await (await client.PutAsJsonAsync(path,
            new UpdateHabitRequest("Read more", "Before bed", new HabitFrequencyRequest("TimesPerWeek", null, 4), Rome))).Content
            .ReadFromJsonAsync<HabitDetailResponse>())!;
        Assert.Equal(("Read more", "Before bed", "TimesPerWeek", Today), (edited.Name, edited.Description, edited.Frequency.Kind, edited.Frequency.EffectiveFrom));
        Assert.Equal("Missed", edited.RecentDays.Single(day => day.Date == Today.AddDays(-1)).Status);

        // Archiving on the day of a frequency change replaces that day's version (HAB-001 §5.1 rule 6):
        // the habit would be restored with its previous frequency.
        var sameDay = (await (await client.PostAsJsonAsync($"{path}/archive", new HabitTodayRequest(Rome))).Content.ReadFromJsonAsync<HabitDetailResponse>())!;
        Assert.Equal("Daily", sameDay.LastActiveFrequency!.Kind);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path)).StatusCode);

        habit = await CreateAsync(client, "Read more", startsOn: Today.AddDays(-7), new HabitFrequencyRequest("TimesPerWeek", null, 4));
        path = $"{Habits}/{habit.Id}";
        var archived = (await (await client.PostAsJsonAsync($"{path}/archive", new HabitTodayRequest(Rome))).Content.ReadFromJsonAsync<HabitDetailResponse>())!;
        Assert.Equal(("Archived", "Inactive", "TimesPerWeek"), (archived.Status, archived.Frequency.Kind, archived.LastActiveFrequency!.Kind));
        Assert.NotNull(archived.ArchivedAtUtc);

        var list = (await client.GetFromJsonAsync<HabitsForDayResponse>($"{Habits}?date=2026-10-14&utcOffsetMinutes={Rome}"))!;
        Assert.Empty(list.Habits);
        Assert.Equal(1, list.ArchivedCount);
        var archivedList = (await client.GetFromJsonAsync<ArchivedHabitsResponse>($"{Habits}/archived"))!;
        Assert.Equal(("Read more", "TimesPerWeek"), (Assert.Single(archivedList.Habits).Name, archivedList.Habits[0].LastActiveFrequency!.Kind));

        await AssertConflictAsync(client.PutAsJsonAsync(path, new UpdateHabitRequest("Read", null, Daily, Rome)), "habit_archived");
        await AssertConflictAsync(client.PutAsJsonAsync($"{path}/check-ins/2026-10-14", new SetHabitCheckInRequest("Done", null, Rome)), "habit_inactive_on_date");

        var restored = (await (await client.PostAsJsonAsync($"{path}/restore", new HabitTodayRequest(Rome))).Content.ReadFromJsonAsync<HabitDetailResponse>())!;
        Assert.Equal(("Active", "TimesPerWeek", 4), (restored.Status, restored.Frequency.Kind, restored.Frequency.TimesPerWeek));
        Assert.Empty((await client.GetFromJsonAsync<ArchivedHabitsResponse>($"{Habits}/archived"))!.Habits);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{path}?utcOffsetMinutes={Rome}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(path)).StatusCode);
    }

    [Fact]
    public async Task AnotherUsersHabit_Is404_LikeAMissingOne_AndStaysUntouched()
    {
        await using var factory = await FactoryAsync();
        var owner = await SignInAsync(factory);
        var other = await SignInAsync(factory);
        var habit = await CreateAsync(owner, "Private", startsOn: Today.AddDays(-3));
        await owner.PutAsJsonAsync($"{Habits}/{habit.Id}/check-ins/2026-10-14", new SetHabitCheckInRequest("Done", null, Rome));
        var path = $"{Habits}/{habit.Id}";
        var missing = $"{Habits}/{Guid.CreateVersion7()}";

        Assert.Empty((await other.GetFromJsonAsync<HabitsForDayResponse>($"{Habits}?date=2026-10-14&utcOffsetMinutes={Rome}"))!.Habits);

        var foreignGet = await other.GetAsync($"{path}?utcOffsetMinutes={Rome}");
        var missingGet = await owner.GetAsync($"{missing}?utcOffsetMinutes={Rome}");
        Assert.Equal(HttpStatusCode.NotFound, foreignGet.StatusCode);
        Assert.Equal(await ProblemTextAsync(missingGet), await ProblemTextAsync(foreignGet));

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{path}/history?from=2026-10-11&to=2026-10-14&utcOffsetMinutes={Rome}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(path, new UpdateHabitRequest("Stolen", null, null, Rome))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{path}/archive", new HabitTodayRequest(Rome))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"{path}/restore", new HabitTodayRequest(Rome))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.PutAsJsonAsync($"{path}/check-ins/2026-10-13", new SetHabitCheckInRequest("Done", null, Rome))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"{path}/check-ins/2026-10-14?utcOffsetMinutes={Rome}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync(path)).StatusCode);
        Assert.Empty((await other.GetFromJsonAsync<ArchivedHabitsResponse>($"{Habits}/archived"))!.Habits);

        var stored = (await owner.GetFromJsonAsync<HabitDetailResponse>($"{path}?utcOffsetMinutes={Rome}"))!;
        Assert.Equal(("Private", "Active", "Done"), (stored.Name, stored.Status, stored.Progress.TodayStatus));
    }

    // B4 (HAB-US11): the water habit is done from Hydration; Habits stores no water and no Done.
    [Fact]
    public async Task WaterHabit_IsDerivedFromHydration_WithConflictCodes()
    {
        await using var factory = await FactoryAsync();
        var client = await SignInAsync(factory);
        var water = Create("Reach my water target") with { CompletionSource = "Hydration" };

        await AssertConflictAsync(client.PostAsJsonAsync(Habits, water), "hydration_target_required");

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/nutrition/hydration/target", new SetHydrationTargetRequest(2000, Rome))).StatusCode);
        var created = await client.PostAsJsonAsync(Habits, water);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var habit = (await created.Content.ReadFromJsonAsync<HabitDetailResponse>())!;
        Assert.Equal(("Hydration", "Pending", 0, 2000), (habit.CompletionSource, habit.Progress.TodayStatus, habit.Hydration!.TotalMl, habit.Hydration.TargetMl));

        await AssertConflictAsync(client.PostAsJsonAsync(Habits, water), "hydration_habit_exists");

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/nutrition/hydration/intakes",
            new AddHydrationIntakeRequest(2000, Today, new TimeOnly(9, 0), Rome))).StatusCode);

        using var list = JsonDocument.Parse(await client.GetStringAsync($"{Habits}?date=2026-10-14&utcOffsetMinutes={Rome}"));
        var row = list.RootElement.GetProperty("habits")[0];
        Assert.Equal(("Done", "Done"), (row.GetProperty("status").GetString(), row.GetProperty("group").GetString()));
        Assert.Equal((2000, 2000), (row.GetProperty("hydration").GetProperty("totalMl").GetInt32(), row.GetProperty("hydration").GetProperty("targetMl").GetInt32()));

        var checkIn = $"{Habits}/{habit.Id}/check-ins/2026-10-14";
        await AssertConflictAsync(client.PutAsJsonAsync(checkIn, new SetHabitCheckInRequest("Done", null, Rome)), "habit_completion_derived");
        var excused = (await (await client.PutAsJsonAsync(checkIn, new SetHabitCheckInRequest("Excused", "Travel", Rome))).Content
            .ReadFromJsonAsync<HabitDayResponse>())!;
        Assert.Equal(("Excused", 2000), (excused.Status, excused.Hydration!.TotalMl));
        Assert.Equal("Done", (await (await client.DeleteAsync($"{checkIn}?utcOffsetMinutes={Rome}")).Content.ReadFromJsonAsync<HabitDayResponse>())!.Status);

        // Archived, another can be created; the first can't be restored while that one is active.
        await client.PostAsJsonAsync($"{Habits}/{habit.Id}/archive", new HabitTodayRequest(Rome));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Habits, water)).StatusCode);
        await AssertConflictAsync(client.PostAsJsonAsync($"{Habits}/{habit.Id}/restore", new HabitTodayRequest(Rome)), "hydration_habit_exists");

        // Manual habits have no "hydration" member at all in the JSON shape's value (null).
        var manual = await CreateAsync(client, "Read");
        Assert.Null(manual.Hydration);
    }

    [Fact]
    public async Task ApiClient_RoundTrip()
    {
        await using var factory = await FactoryAsync();
        var http = await SignInAsync(factory);
        var api = new LifeOS.App.Services.Habits.HabitsApiClient(http, () => Rome);

        var created = await api.CreateAsync(new CreateHabitRequest("Stretch", null, "Manual", Daily, Today.AddDays(-1), Rome));
        Assert.True(created.IsSuccess, string.Join(" ", created.Errors));
        var id = created.Value!.Id;

        var done = await api.SetCheckInAsync(id, Today, "Done", null);
        Assert.Equal("Done", done.Value!.Status);
        Assert.Equal("Done", (await api.GetDayAsync(Today)).Value!.Habits.Single().Status);
        Assert.Equal("Pending", (await api.RemoveCheckInAsync(id, Today)).Value!.Status);
        Assert.Equal(7, (await api.GetDetailAsync(id, 7)).Value!.Progress.Adherence.WindowDays);
        Assert.Equal(2, (await api.GetHistoryAsync(id, Today.AddDays(-1), Today)).Value!.Days.Count);
        Assert.Equal("Archived", (await api.ArchiveAsync(id)).Value!.Status);
        Assert.Single((await api.GetArchivedAsync()).Value!.Habits);
        Assert.Equal("Active", (await api.RestoreAsync(id)).Value!.Status);
        Assert.Equal("Walk", (await api.UpdateAsync(id, new UpdateHabitRequest("Walk", null, null, Rome))).Value!.Name);

        var invalid = await api.SetCheckInAsync(id, Today.AddDays(1), "Done", null);
        Assert.Equal(["You can't check in for a future date."], invalid.Errors);

        Assert.True((await api.DeleteAsync(id)).IsSuccess);
        Assert.True(LifeOS.App.Services.Habits.HabitsApiClient.IsNotFound(await api.GetDetailAsync(id, 30)));
        // Already gone counts as deleted.
        Assert.True((await api.DeleteAsync(id)).IsSuccess);
    }

    // ---- Helpers ----

    private static CreateHabitRequest Create(string name) => new(name, null, "Manual", Daily, null, Rome);

    private static async Task<HabitDetailResponse> CreateAsync(HttpClient client, string name, DateOnly? startsOn = null,
        HabitFrequencyRequest? frequency = null)
    {
        var response = await client.PostAsJsonAsync(Habits, Create(name) with { StartsOn = startsOn, Frequency = frequency ?? Daily });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<HabitDetailResponse>())!;
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

    private async Task<HabitApiFactory> FactoryAsync()
    {
        await using var scope = fixture.CreateScope();

        return new HabitApiFactory(scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.GetConnectionString()!);
    }

    private static async Task<HttpClient> SignInAsync(HabitApiFactory factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/dev/sign-in", new DevSignInRequest("habit-test-" + Guid.NewGuid(), null, null));
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken);

        return client;
    }

    private sealed class HabitApiFactory(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                // Freeze the habits' "today" without changing JWT issuance/validation clocks.
                var clock = new FixedTimeProvider(Now);
                static IHabitRepository Habits(IServiceProvider provider) => provider.GetRequiredService<IHabitRepository>();
                static IHydrationDayReader Water(IServiceProvider provider) => provider.GetRequiredService<IHydrationDayReader>();
                Replace(services, provider => new GetHabitsForDayHandler(Habits(provider), Water(provider), clock));
                Replace(services, provider => new GetHabitDetailHandler(Habits(provider), Water(provider), clock));
                Replace(services, provider => new GetHabitHistoryHandler(Habits(provider), Water(provider), clock));
                Replace(services, provider => new CreateHabitHandler(Habits(provider), Water(provider), clock));
                Replace(services, provider => new UpdateHabitHandler(Habits(provider), Water(provider), clock));
                Replace(services, provider => new ArchiveHabitHandler(Habits(provider), Water(provider), clock));
                Replace(services, provider => new SetHabitCheckInHandler(Habits(provider), Water(provider), clock));
                Replace(services, provider => new RemoveHabitCheckInHandler(Habits(provider), Water(provider), clock));

                // The water habit's target and intakes on the same frozen day.
                Replace(services, provider => new SetHydrationTargetHandler(provider.GetRequiredService<IHydrationRepository>(), clock));
                Replace(services, provider => new AddHydrationIntakeHandler(provider.GetRequiredService<IHydrationRepository>(),
                    provider.GetRequiredService<HydrationDayReader>(), clock));
            });
            builder.UseSetting("ConnectionStrings:PostgreSQL", connection);
            builder.UseSetting("Authentication:LifeOS:SigningKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting(LifeOS.Api.Authentication.DevelopmentSignIn.EnabledKey, "true");
            builder.UseSetting("Authentication:Google:ClientId", "test-client-id.apps.googleusercontent.com");
            builder.UseSetting("Authentication:Google:ClientSecret", "test-client-secret");
            builder.UseSetting("Authentication:Google:AllowedEmails:0", "person@example.com");
        }

        private static void Replace<T>(IServiceCollection services, Func<IServiceProvider, T> factory) where T : class
        {
            services.RemoveAll<T>();
            services.AddScoped(factory);
        }
    }
}
