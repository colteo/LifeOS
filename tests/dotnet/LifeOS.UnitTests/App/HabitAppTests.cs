using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LifeOS.App.Services;
using LifeOS.App.Services.Habits;
using LifeOS.App.Services.Navigation;
using LifeOS.Contracts.Habits;

namespace LifeOS.UnitTests.App;

// HAB-001 B3 app: the Habits entry in Modules, the Today/new/edit/detail/archived pages, the Home card,
// the API client (paths, offset, errors), the editor draft and the display rules. Razor is checked by
// source, as in JournalAppTests; the dock rules are also checked here.
public class HabitAppTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);
    private static readonly Guid Id = new("0198c0de-0000-7000-8000-000000000042");

    private static readonly HabitFrequencyResponse Daily = new("Daily", null, null, new DateOnly(2026, 10, 1));
    private static readonly HabitFrequencyResponse MonWedFri = new("Weekdays", ["Monday", "Wednesday", "Friday"], null, new DateOnly(2026, 10, 1));
    private static readonly HabitFrequencyResponse ThreeAWeek = new("TimesPerWeek", null, 3, new DateOnly(2026, 10, 1));

    // ---- Navigation ----

    [Fact]
    public void Modules_ListsHabits_AndTheDockIsUnchanged()
    {
        Assert.Contains("new(\"Habits\", \"Small daily habits and streaks\", \"habits\", \"habits\")", Source("Pages", "More.razor"));
        Assert.Contains("case \"habits\":", Source("Shared", "AppIcon.razor"));
        Assert.Equal(["Home", "Transactions", "Quick add", "Nutrition", "More"], DockNavigation.Items.Select(item => item.Label));
        Assert.DoesNotContain("habits", Source("Layout", "BottomDock.razor"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("habits", Source("Layout", "AppHeader.razor"), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("habits")]
    [InlineData("habits/new")]
    [InlineData("habits/archived")]
    [InlineData("habits/0198c0de-0000-7000-8000-000000000001")]
    [InlineData("habits/0198c0de-0000-7000-8000-000000000001/edit")]
    public void HabitPages_SelectMore(string path) =>
        Assert.Equal(["More"], DockNavigation.Items.Where(item => item.IsActive(DockNavigation.Normalize(path))).Select(item => item.Label));

    [Fact]
    public void HabitsLookalike_IsNotMore() =>
        Assert.DoesNotContain(DockNavigation.Items, item => item.IsActive(DockNavigation.Normalize("habitsx")));

    [Fact]
    public void Pages_AreRouted_AndBackLinksFollowTheHierarchy()
    {
        Assert.StartsWith("@page \"/habits\"", Page("Habits.razor"));
        Assert.StartsWith("@page \"/habits/new\"", Page("NewHabit.razor"));
        Assert.StartsWith("@page \"/habits/{HabitId:guid}/edit\"", Page("EditHabit.razor"));
        Assert.StartsWith("@page \"/habits/{HabitId:guid}\"", Page("HabitDetail.razor"));
        Assert.StartsWith("@page \"/habits/archived\"", Page("ArchivedHabits.razor"));

        Assert.Contains("<PageHeader Title=\"@HabitDisplay.Title\" BackHref=\"more\" />", Page("Habits.razor"));
        Assert.Contains("<PageHeader Title=\"New habit\" BackHref=\"habits\" />", Page("NewHabit.razor"));
        Assert.Contains("<PageHeader Title=\"Edit habit\" BackHref=\"@DetailHref\" />", Page("EditHabit.razor"));
        Assert.Contains("<PageHeader Title=\"Archived habits\" BackHref=\"habits\" />", Page("ArchivedHabits.razor"));
        Assert.Contains("BackHref=\"@(detail?.Status == \"Archived\" ? \"habits/archived\" : \"habits\")\"", Page("HabitDetail.razor"));
    }

    [Fact]
    public void Client_IsRegisteredOnTheAuthorizedPipeline_WithTheDeviceOffset()
    {
        var program = File.ReadAllText(Path.Combine(ComponentsRoot(), "..", "MauiProgram.cs"));

        Assert.Contains("new LifeOS.App.Services.Habits.HabitsApiClient(", program);
        Assert.Contains("CreateAuthorizedHttpClient(services), LifeOS.App.Services.Habits.HabitsApiClient.DeviceUtcOffsetMinutes)", program);
    }

    // ---- Today page ----

    [Fact]
    public void Today_HasChipsSectionsAndStates_AndNoPlaceholders()
    {
        var page = Page("Habits.razor");

        Assert.Contains("HabitDisplay.ChipDates(today)", page);
        Assert.Contains("aria-pressed=\"@(date == selectedDay ? \"true\" : \"false\")\"", page);
        Assert.Contains("Loading habits...", page);
        Assert.Contains("Retry", page);
        Assert.Contains("@HabitDisplay.EmptyTitle", page);
        Assert.Contains("<a href=\"habits/new\"", page);
        Assert.Contains("@foreach (var (group, _) in HabitDisplay.Sections)", page);
        Assert.Contains("Upcoming (@day.UpcomingCount)", page);
        Assert.Contains("<a href=\"habits/archived\"", page);
        Assert.DoesNotMatch(new Regex(@"href=""#""|TODO|Coming soon", RegexOptions.IgnoreCase), page);
    }

    [Fact]
    public void Today_OneTapIsOptimistic_AndRolledBackOnError()
    {
        var page = Page("Habits.razor");

        Assert.Contains("return SetAsync(item, \"Done\", null);", page);
        Assert.Contains("Replace(item.Id, HabitDisplay.Optimistic(item, status, date, today));", page);
        Assert.Contains("Replace(item.Id, item);", page);
        Assert.Contains("actionError = $\"{item.Name}: {string.Join(\" \", result.Errors)}\";", page);
        Assert.Contains("aria-live=\"polite\"", page);
        Assert.Contains("HabitDisplay.Announcement(item.Name, result.Value!)", page);
        Assert.Contains("aria-label=\"@HabitDisplay.ToggleLabel(item.Name, item.Status, selectedDay, today, HabitDisplay.IsDerived(item.CompletionSource))\"", page);
    }

    [Fact]
    public void Today_OptionsOffer_UndoExcuseAndMarkDone()
    {
        var page = Page("Habits.razor");

        Assert.Contains(">Undo</button>", page);
        Assert.Contains("HabitDisplay.CanExcuse(item.Frequency, selectedDay)", page);
        Assert.Contains(">Excuse day</button>", page);
        Assert.Contains(">Mark done</button>", page);
        Assert.Contains("HabitsApi.RemoveCheckInAsync(item.Id, selectedDay, lifetime.Token)", page);
    }

    // ---- Editor ----

    [Fact]
    public void Editor_HasTheFields_AndFrequencyControls()
    {
        var form = Source("Habits", "HabitForm.razor");

        Assert.Contains("Name", form);
        Assert.Contains("Description (optional)", form);
        Assert.Contains("How often", form);
        Assert.Contains("@foreach (var day in HabitDisplay.Week)", form);
        Assert.Contains("Times per week (Monday to Sunday)", form);
        Assert.Contains("type=\"date\"", form);
        Assert.Contains("@if (!Draft.IsEditing)", form);
        Assert.Contains("Applies from today; past days keep their schedule.", form);
        Assert.Contains("Restore the habit to change how often it is expected.", form);
        Assert.Contains("$\"habits/{result.Value!.Id}\"", form);

        Assert.Contains("HabitsApi.CreateAsync(values.ToCreateRequest(HabitsApiClient.DeviceUtcOffsetMinutes()))", Page("NewHabit.razor"));
        Assert.Contains("HabitsApi.UpdateAsync(HabitId, values.ToUpdateRequest(HabitsApiClient.DeviceUtcOffsetMinutes()))", Page("EditHabit.razor"));
        // B4: the type (manual or the water template) is chosen when creating only.
        Assert.Contains("@foreach (var (source, label) in HabitDraft.Sources)", form);
        Assert.Contains("Draft.SetCompletionSource(source)", form);
        Assert.Contains("@HabitDisplay.WaterHabitExplanation @HabitDisplay.WaterHabitNeedsTarget", form);
        Assert.Contains("href=\"nutrition/targets\"", form);
    }

    // ---- Water habit (B4, HAB-US11) ----

    [Fact]
    public void WaterDraft_SendsTheHydrationSource_AndFillsTheName()
    {
        var draft = HabitDraft.ForNew(Today);
        Assert.Equal("Manual", draft.ToCreateRequest(120).CompletionSource);

        draft.SetCompletionSource(HabitDraft.Hydration);
        Assert.True(draft.IsWater);
        Assert.Equal(("Reach my water target", "Hydration"), (draft.Name, draft.ToCreateRequest(120).CompletionSource));

        // Back to manual clears only the untouched template name.
        draft.SetCompletionSource(HabitDraft.Manual);
        Assert.Equal("", draft.Name);
        draft.Name = "Drink";
        draft.SetCompletionSource(HabitDraft.Hydration);
        Assert.Equal("Drink", draft.Name);
    }

    [Fact]
    public void WaterRow_ShowsTheApiTotals_AndIsNeverCheckedInByHand()
    {
        var row = Item("Pending", "ToDo", Daily) with { CompletionSource = "Hydration", Hydration = new HabitHydrationResponse(1250, 2000) };

        Assert.Equal("1250 / 2000 ml · Every day · 0 days", HabitDisplay.RowDetail(row));
        Assert.Equal("800 ml · no target", HabitDisplay.WaterText(new HabitHydrationResponse(800, null)));
        Assert.True(HabitDisplay.IsDerived(row.CompletionSource));
        Assert.Equal("Reach my water target, to do today, open options",
            HabitDisplay.ToggleLabel("Reach my water target", "Pending", Today, Today, derived: true));

        var page = Page("Habits.razor");
        Assert.Contains("HabitDisplay.IsChecked(item.Status) || HabitDisplay.IsDerived(item.CompletionSource)", page);
        Assert.Contains("item.Status != \"Done\" && !HabitDisplay.IsDerived(item.CompletionSource)", page);
        Assert.Contains("<a href=\"nutrition\">Log water</a>", page);

        var detail = Page("HabitDetail.razor");
        Assert.Contains("Today: @HabitDisplay.WaterText(water)", detail);
        Assert.Contains("<a href=\"nutrition\" class=\"btn btn-outline-secondary\">Log water</a>", detail);
    }

    [Fact]
    public void Draft_ValidatesWithTheApiLimits()
    {
        var draft = HabitDraft.ForNew(Today);
        Assert.Equal(["Give the habit a name."], draft.Validate());

        draft.Name = new string('n', 81);
        draft.Description = new string('d', 501);
        draft.Kind = HabitDraft.Weekdays;
        foreach (var day in draft.SelectedWeekdays.ToList())
        {
            draft.ToggleWeekday(day);
        }

        Assert.Equal(
            ["The name must be at most 80 characters.", "The description must be at most 500 characters.", "Choose at least one day of the week."],
            draft.Validate());

        draft.Name = "  Read  ";
        draft.Description = "  ";
        draft.Kind = HabitDraft.TimesPerWeek;
        draft.TimesPerWeekValue = 8;
        Assert.Equal(["Choose between 1 and 7 times per week."], draft.Validate());
    }

    [Fact]
    public void Draft_BuildsTheCreateRequest()
    {
        var draft = HabitDraft.ForNew(Today);
        draft.Name = "  Read 20 pages ";
        draft.Kind = HabitDraft.Weekdays;
        draft.ToggleWeekday(DayOfWeek.Sunday);
        draft.SetStartsOnFromPicker("2026-10-10");
        draft.SetStartsOnFromPicker("not a date");

        var request = draft.ToCreateRequest(120);

        Assert.Equal(("Read 20 pages", (string?)null, "Manual", new DateOnly(2026, 10, 10), (int?)120),
            (request.Name, request.Description, request.CompletionSource, request.StartsOn, request.UtcOffsetMinutes));
        Assert.Equal("Weekdays", request.Frequency!.Kind);
        Assert.Equal(["Monday", "Wednesday", "Friday", "Sunday"], request.Frequency.Weekdays!);
        Assert.Equal("2026-10-10", draft.StartsOnText);
    }

    [Fact]
    public void Draft_FromAHabit_SendsTheFrequencyOnlyWhenChanged()
    {
        var draft = HabitDraft.From(Detail(MonWedFri));

        Assert.True(draft.IsEditing);
        Assert.Equal((HabitDraft.Weekdays, true), (draft.Kind, draft.IsSelected(DayOfWeek.Wednesday)));
        Assert.Null(draft.ToUpdateRequest(60).Frequency);

        draft.ToggleWeekday(DayOfWeek.Friday);
        Assert.True(draft.FrequencyChanged);
        Assert.Equal(["Monday", "Wednesday"], draft.ToUpdateRequest(60).Frequency!.Weekdays!);

        // Archived: the last frequency is shown read-only and never sent.
        var archived = HabitDraft.From(Detail(new HabitFrequencyResponse("Inactive", null, null, Today), "Archived", ThreeAWeek));
        Assert.Equal((false, HabitDraft.TimesPerWeek, 3), (archived.FrequencyEditable, archived.Kind, archived.TimesPerWeekValue));
        archived.TimesPerWeekValue = 5;
        Assert.Null(archived.ToUpdateRequest(60).Frequency);
        Assert.Empty(archived.Validate());
    }

    // ---- Display ----

    [Fact]
    public void Chips_AreTodayAndTheSixDaysBefore_NeverTheFuture()
    {
        var dates = HabitDisplay.ChipDates(Today);

        Assert.Equal(7, dates.Count);
        Assert.Equal(Today, dates[0]);
        Assert.Equal(Today.AddDays(-6), dates[^1]);
        Assert.DoesNotContain(dates, date => date > Today);
        Assert.Equal(["Today", "Yesterday", "Mon 12", "Sun 11"], dates.Take(4).Select(date => HabitDisplay.ChipLabel(date, Today)));
    }

    [Fact]
    public void Text_IsNeutralAndReadable()
    {
        Assert.Equal("Every day", HabitDisplay.FrequencyText(Daily));
        Assert.Equal("Mon, Wed, Fri", HabitDisplay.FrequencyText(MonWedFri));
        Assert.Equal("3 times a week", HabitDisplay.FrequencyText(ThreeAWeek));
        Assert.Equal("Once a week", HabitDisplay.FrequencyText(ThreeAWeek with { TimesPerWeek = 1 }));
        Assert.Equal("Every day · since 1 Oct", HabitDisplay.FrequencySince(Daily, Today));
        Assert.Equal(("1 day", "4 days", "1 week", "2 weeks"),
            (HabitDisplay.StreakText(1, "Day"), HabitDisplay.StreakText(4, "Day"), HabitDisplay.StreakText(1, "Week"), HabitDisplay.StreakText(2, "Week")));
        Assert.Equal("This week 4 / 3", HabitDisplay.WeekText(new HabitWeekResponse(Today, 4, 3)));
        Assert.Equal("21 of 26 days · 81%", HabitDisplay.AdherenceText(new HabitAdherenceResponse(21, 26, 81, 30), "Day"));
        Assert.Equal("5 of 6 check-ins · 83%", HabitDisplay.AdherenceText(new HabitAdherenceResponse(5, 6, 83, 30), "Week"));
        Assert.Equal("Not enough data yet", HabitDisplay.AdherenceText(new HabitAdherenceResponse(0, 0, null, 30), "Day"));

        Assert.Equal(["Done", "Excused", "Missed", "To do", "Not scheduled", "Inactive"],
            new[] { "Done", "Excused", "Missed", "Pending", "NotScheduled", "Inactive" }.Select(HabitDisplay.StatusLabel));
    }

    [Fact]
    public void AccessibleNames_SayWhatTheToggleDoes()
    {
        Assert.Equal("Mark Read done for today", HabitDisplay.ToggleLabel("Read", "Pending", Today, Today));
        Assert.Equal("Read, done yesterday, open options", HabitDisplay.ToggleLabel("Read", "Done", Today.AddDays(-1), Today));
        Assert.Equal("Mark Read done for Mon 12 Oct", HabitDisplay.ToggleLabel("Read", "Missed", Today.AddDays(-2), Today));

        var day = new HabitDayResponse(Id, Today, "Done", null, Progress(4));
        Assert.Equal("Read done. Streak 4 days.", HabitDisplay.Announcement("Read", day));
    }

    [Fact]
    public void Excuse_IsOfferedOnlyOnExpectedDays()
    {
        Assert.True(HabitDisplay.CanExcuse(Daily, Today));
        Assert.True(HabitDisplay.CanExcuse(ThreeAWeek, Today));
        Assert.True(HabitDisplay.CanExcuse(MonWedFri, Today));             // Wednesday
        Assert.False(HabitDisplay.CanExcuse(MonWedFri, Today.AddDays(-1))); // Tuesday
    }

    [Fact]
    public void Optimistic_Rows_MoveBetweenGroups()
    {
        var pending = Item("Pending", "ToDo", Daily);

        Assert.Equal(("Done", "Done"), Pair(HabitDisplay.Optimistic(pending, "Done", Today, Today)));
        Assert.Equal(("Pending", "ToDo"), Pair(HabitDisplay.Optimistic(pending with { Status = "Done", Group = "Done" }, "None", Today, Today)));
        Assert.Equal(("Missed", "ToDo"), Pair(HabitDisplay.Optimistic(pending with { Status = "Done", Group = "Done" }, "None", Today.AddDays(-1), Today)));
        Assert.Equal(("NotScheduled", "NotToday"),
            Pair(HabitDisplay.Optimistic(Item("Done", "Done", MonWedFri), "None", Today.AddDays(-1), Today)));

        // A weekly goal: the third check-in completes the week.
        var weekly = Item("Pending", "ToDo", ThreeAWeek) with { ThisWeek = new HabitWeekResponse(Today, 2, 3) };
        var done = HabitDisplay.Optimistic(weekly, "Done", Today, Today);
        Assert.Equal(("Done", "Done", 3), (done.Status, done.Group, done.ThisWeek!.Done));
        var undone = HabitDisplay.Optimistic(weekly with { Status = "NotScheduled", ThisWeek = new HabitWeekResponse(Today, 1, 3) }, "Done",
            Today.AddDays(-1), Today);
        Assert.Equal(2, undone.ThisWeek!.Done);

        static (string, string) Pair(HabitListItemResponse item) => (item.Status, item.Group);
    }

    [Fact]
    public void Calendar_FiveMondayToSundayWeeks_FutureDaysHaveNoStatus()
    {
        var from = HabitDisplay.IsoWeekStart(Today).AddDays(-28);
        var weeks = HabitDisplay.CalendarWeeksFrom(from, HabitDisplay.CalendarWeeks, Today,
            [new HabitDayItemResponse(Today.AddDays(-1), "Missed", null), new HabitDayItemResponse(Today, "Done", null),
             new HabitDayItemResponse(Today.AddDays(-2), "Excused", "Sick")]);

        Assert.Equal(new DateOnly(2026, 9, 14), from);
        Assert.Equal(5, weeks.Count);
        Assert.All(weeks, week => Assert.Equal(DayOfWeek.Monday, week[0].Date.DayOfWeek));
        var current = weeks[^1];
        Assert.Equal("Tue 13 Oct, Missed", HabitDisplay.CellLabel(current[1]));
        Assert.Equal("Mon 12 Oct, Excused: Sick", HabitDisplay.CellLabel(current[0]));
        Assert.True(current[2].IsToday);
        Assert.True(current[3].IsFuture);
        Assert.Equal("Thu 15 Oct, upcoming", HabitDisplay.CellLabel(current[3]));
        Assert.False(HabitDisplay.CanChange(current[3]));
        Assert.True(HabitDisplay.CanChange(current[1]));
        Assert.False(HabitDisplay.CanChange(current[1] with { Status = "Inactive" }));
        Assert.Equal(new DateOnly(2026, 12, 28), HabitDisplay.IsoWeekStart(new DateOnly(2027, 1, 3)));
    }

    [Fact]
    public void Detail_ShowsStatsCalendarAndActions()
    {
        var page = Page("HabitDetail.razor");

        Assert.Contains("Current streak", page);
        Assert.Contains("Best streak", page);
        Assert.Contains("This week", page);
        Assert.Contains("@foreach (var option in HabitDisplay.WindowOptions)", page);
        Assert.Contains("HabitDisplay.AdherenceText(progress.Adherence, progress.StreakUnit)", page);
        Assert.Contains("role=\"progressbar\"", page);
        Assert.Contains("<HabitCalendar Weeks=\"Weeks\"", page);
        Assert.Contains("Show earlier weeks", page);
        Assert.Contains("HabitsApi.GetHistoryAsync(HabitId, from, to, lifetime.Token)", page);
        Assert.Contains("Edit habit", page);
        Assert.Contains("Archive habit", page);
        Assert.Contains("Restore habit", page);
        Assert.Contains("Delete “@detail.Name” and its history? This can't be undone.", page);
        Assert.Contains("@HabitsApiClient.NotFoundMessage", page);

        var calendar = Source("Habits", "HabitCalendar.razor");
        Assert.Contains("aria-label=\"@HabitDisplay.CellLabel(cell)\"", calendar);
        Assert.Contains("@if (HabitDisplay.CanChange(cell))", calendar);
    }

    [Fact]
    public void Archived_ListsAndRestores()
    {
        var page = Page("ArchivedHabits.razor");

        Assert.Contains("HabitsApi.GetArchivedAsync(lifetime.Token)", page);
        Assert.Contains("HabitsApi.RestoreAsync(habit.Id, lifetime.Token)", page);
        Assert.Contains("No archived habits.", page);
    }

    // ---- Home ----

    [Fact]
    public void HomeCard_AppearsOnlyWithActiveHabits()
    {
        var card = Source("Habits", "HomeHabitsCard.razor");

        Assert.Contains("<HomeHabitsCard />", Source("Pages", "Home.razor"));
        Assert.Contains("@if (day is not null && HasActiveHabits)", card);
        Assert.Contains("private bool HasActiveHabits => day!.Habits.Count + day.UpcomingCount > 0;", card);
        Assert.Contains("Habits today", card);
        Assert.Contains("<a href=\"habits\"", card);

        Assert.Equal("2 of 3 done", HabitDisplay.HomeSummary(
            [Item("Done", "Done", Daily), Item("Excused", "Done", Daily), Item("Pending", "ToDo", Daily), Item("NotScheduled", "NotToday", MonWedFri)]));
        Assert.Equal("Nothing scheduled today", HabitDisplay.HomeSummary([Item("NotScheduled", "NotToday", MonWedFri)]));
    }

    // ---- API client ----

    [Fact]
    public async Task Client_SendsTheDeviceOffset_AndUsesTheContractPaths()
    {
        var requests = new List<(HttpMethod, string, string?)>();
        var client = new HabitsApiClient(Http(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content?.ReadAsStringAsync().Result));
            return request.Method == HttpMethod.Delete && !request.RequestUri.PathAndQuery.Contains("check-ins")
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : Json(new HabitDayResponse(Id, Today, "Done", null, Progress(1)));
        }), () => -300);

        await client.GetDayAsync(Today);
        await client.GetDetailAsync(Id, 90);
        await client.GetHistoryAsync(Id, Today.AddDays(-34), Today);
        await client.SetCheckInAsync(Id, Today, "Excused", "Sick");
        await client.RemoveCheckInAsync(Id, Today);
        await client.ArchiveAsync(Id);
        await client.DeleteAsync(Id);

        Assert.Equal((HttpMethod.Get, "/api/habits?date=2026-10-14&utcOffsetMinutes=-300"), (requests[0].Item1, requests[0].Item2));
        Assert.Equal("/api/habits/0198c0de-0000-7000-8000-000000000042?utcOffsetMinutes=-300&window=90", requests[1].Item2);
        Assert.Equal("/api/habits/0198c0de-0000-7000-8000-000000000042/history?from=2026-09-10&to=2026-10-14&utcOffsetMinutes=-300", requests[2].Item2);
        Assert.Equal((HttpMethod.Put, "/api/habits/0198c0de-0000-7000-8000-000000000042/check-ins/2026-10-14"), (requests[3].Item1, requests[3].Item2));
        Assert.Contains("\"utcOffsetMinutes\":-300", requests[3].Item3);
        Assert.Contains("\"status\":\"Excused\"", requests[3].Item3);
        Assert.Equal((HttpMethod.Delete, "/api/habits/0198c0de-0000-7000-8000-000000000042/check-ins/2026-10-14?utcOffsetMinutes=-300"),
            (requests[4].Item1, requests[4].Item2));
        Assert.Contains("\"utcOffsetMinutes\":-300", requests[5].Item3);
        Assert.Equal((HttpMethod.Delete, "/api/habits/0198c0de-0000-7000-8000-000000000042"), (requests[6].Item1, requests[6].Item2));
    }

    [Fact]
    public async Task Client_ReportsReadableErrors()
    {
        var notFound = await new HabitsApiClient(Http(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), () => 0).GetDetailAsync(Id, 30);
        Assert.True(HabitsApiClient.IsNotFound(notFound));

        var conflict = await new HabitsApiClient(Http(_ => Problem(HttpStatusCode.Conflict,
            """{"title":"The habit can't be changed this way.","detail":"The habit was archived on this date.","status":409,"code":"habit_inactive_on_date"}""")),
            () => 0).SetCheckInAsync(Id, Today, "Done", null);
        Assert.Equal(["The habit was archived on this date."], conflict.Errors);

        var invalid = await new HabitsApiClient(Http(_ => Problem(HttpStatusCode.BadRequest,
            """{"title":"One or more validation errors occurred.","status":400,"errors":{"name":["Give the habit a name."]}}""")),
            () => 0).CreateAsync(new CreateHabitRequest("", null, "Manual", new HabitFrequencyRequest("Daily", null, null), null, 0));
        Assert.Equal(["Give the habit a name."], invalid.Errors);

        var unreachable = await new HabitsApiClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down")))
            { BaseAddress = new Uri("https://lifeos.test/") }, () => 0).GetDayAsync(Today);
        Assert.Equal([ApiErrors.UnreachableMessage], unreachable.Errors);

        // Deleting a habit that is already gone is the outcome the user asked for.
        Assert.True((await new HabitsApiClient(Http(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), () => 0).DeleteAsync(Id)).IsSuccess);
    }

    // ---- Helpers ----

    private static HabitProgressResponse Progress(int streak) =>
        new(Today, "Done", streak, streak, "Day", new HabitAdherenceResponse(1, 1, 100, 30), null);

    private static HabitListItemResponse Item(string status, string group, HabitFrequencyResponse frequency) =>
        new(Guid.CreateVersion7(), "Read", "Manual", status, null, group, 0, frequency.Kind == "TimesPerWeek" ? "Week" : "Day", frequency, null);

    private static HabitDetailResponse Detail(HabitFrequencyResponse frequency, string status = "Active", HabitFrequencyResponse? lastActive = null) =>
        new(Id, "Read", "Before bed", "Manual", status, new DateOnly(2026, 10, 1), frequency, lastActive ?? frequency,
            DateTimeOffset.UnixEpoch, status == "Archived" ? DateTimeOffset.UnixEpoch : null, Progress(0), []);

    private static string Page(string fileName) => Source("Pages", Path.Combine("Habits", fileName));

    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new StubHandler(request => Task.FromResult(send(request)))) { BaseAddress = new Uri("https://lifeos.test/") };

    private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value) };

    private static HttpResponseMessage Problem(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/problem+json") };

    private static string ComponentsRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components"));

    private static string Source(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(ComponentsRoot(), folder, fileName));

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
