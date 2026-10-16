using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using LifeOS.App.Services;
using LifeOS.App.Services.Navigation;
using LifeOS.App.Services.Notifications;
using LifeOS.App.Services.Reminders;
using LifeOS.Contracts.Notifications;
using LifeOS.Contracts.Reminders;

namespace LifeOS.UnitTests.App;

// REM-001 R4 app: the Reminders entry in Modules, the list/new/edit/detail pages, the API client (paths,
// offset, errors), the editor draft, the display rules, the notification tap and the Settings switch.
// Razor is checked by source, as in HabitAppTests.
public class ReminderAppTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);
    private static readonly DateTime LocalNow = new(2026, 10, 14, 9, 20, 0);
    private static readonly Guid Id = new("0198c0de-0000-7000-8000-000000000077");

    private static ReminderResponse Reminder(ReminderScheduleResponse? schedule = null, string status = "Active", string? next = "2026-10-15T08:30",
        string? lastOutcome = null, DateTimeOffset? lastAt = null, DateTimeOffset? snooze = null) =>
        new(Id, "Custom", "Take vitamins", "Take vitamins", null, schedule ?? new ReminderScheduleResponse("Daily", null, null, "08:30"), status, next,
            snooze, lastAt, lastOutcome, false);

    // ---- Navigation ----

    [Fact]
    public void Modules_ListsReminders_AndTheDockIsUnchanged()
    {
        Assert.Contains("new(\"Reminders\", \"Your own reminders, once or repeating\", \"reminders\", \"reminders\")", Source("Pages", "More.razor"));
        Assert.Contains("case \"reminders\":", Source("Shared", "AppIcon.razor"));
        Assert.Equal(["Home", "Transactions", "Quick add", "Nutrition", "More"], DockNavigation.Items.Select(item => item.Label));
        Assert.DoesNotContain("reminders", Source("Layout", "BottomDock.razor"), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("reminders")]
    [InlineData("reminders/new")]
    [InlineData("reminders/0198c0de-0000-7000-8000-000000000001")]
    [InlineData("reminders/0198c0de-0000-7000-8000-000000000001/edit")]
    public void ReminderPages_SelectMore(string path) =>
        Assert.Equal(["More"], DockNavigation.Items.Where(item => item.IsActive(DockNavigation.Normalize(path))).Select(item => item.Label));

    [Fact]
    public void Pages_AreRouted_AndBackLinksFollowTheHierarchy()
    {
        Assert.StartsWith("@page \"/reminders\"", Page("Reminders.razor"));
        Assert.StartsWith("@page \"/reminders/new\"", Page("NewReminder.razor"));
        Assert.StartsWith("@page \"/reminders/{ReminderId:guid}/edit\"", Page("EditReminder.razor"));
        Assert.StartsWith("@page \"/reminders/{ReminderId:guid}\"", Page("ReminderDetail.razor"));

        Assert.Contains("<PageHeader Title=\"@ReminderDisplay.Title\" BackHref=\"more\" />", Page("Reminders.razor"));
        Assert.Contains("<PageHeader Title=\"New reminder\" BackHref=\"reminders\" />", Page("NewReminder.razor"));
        Assert.Contains("<PageHeader Title=\"Edit reminder\" BackHref=\"@DetailHref\" />", Page("EditReminder.razor"));
        Assert.Contains("<PageHeader Title=\"Reminder\" BackHref=\"reminders\" />", Page("ReminderDetail.razor"));
    }

    [Fact]
    public void List_ShowsTheThreeBanners_AndTheGroups()
    {
        var list = Page("Reminders.razor");

        Assert.Contains("ReminderDisplay.NotificationsOffBanner", list);
        Assert.Contains("ReminderDisplay.PushUnavailableBanner", list);
        Assert.Contains("ReminderDisplay.TimeZoneUnknownBanner", list);
        Assert.Contains("!overview.PushAvailable", list);
        Assert.Contains("!overview.TimeZoneKnown", list);
        Assert.Contains("Devices.AreNotificationsEnabled()", list);
        Assert.Contains("overview.Upcoming", list);
        Assert.Contains("overview.Paused", list);
        Assert.Contains("overview.Completed", list);
    }

    [Fact]
    public void Detail_OffersSnoozePauseResumeEditAndConfirmedDelete()
    {
        var detail = Page("ReminderDetail.razor");

        Assert.Contains("ReminderDisplay.SnoozeOptions", detail);
        Assert.Contains("aria-label=\"@accessible\"", detail);
        Assert.Contains("RemindersApi.SnoozeAsync(ReminderId, minutes", detail);
        Assert.Contains("RemindersApi.PauseAsync(ReminderId", detail);
        Assert.Contains("RemindersApi.ResumeAsync(ReminderId", detail);
        Assert.Contains("reminders/{reminder.Id}/edit", detail);
        Assert.Contains("role=\"alertdialog\"", detail);
        Assert.Contains("This can't be undone.", detail);
        Assert.Contains("@if (!paused)", detail);
    }

    [Fact]
    public void Editor_WarnsAboutQuietHours_AndSaysTheNoteStaysInTheApp()
    {
        var form = Source("Reminders", "ReminderForm.razor");

        Assert.Contains("ReminderDisplay.QuietHoursWarning(Draft.Time, QuietHoursStart, QuietHoursEnd)", form);
        Assert.Contains("never in the notification", form);
        Assert.Contains("aria-pressed", form);
        Assert.Contains("aria-label=\"@day\"", form);
        Assert.Contains("PreferencesApi.GetAsync()", Page("NewReminder.razor"));
    }

    // ---- Notification tap ----

    [Fact]
    public void TappingAReminder_OpensItsPage_InvalidIdsAreIgnored()
    {
        var target = NotificationTap.Parse("reminder", Id.ToString("D"));

        Assert.Equal(new NotificationTarget(NotificationTargetKind.Reminder, Id), target);
        Assert.Equal($"reminders/{Id:D}", NotificationTap.PathFor(target!));
        Assert.Null(NotificationTap.Parse("reminder", "not-a-guid"));
        Assert.Null(NotificationTap.Parse("reminder", Guid.Empty.ToString()));
        Assert.Null(NotificationTap.Parse("reminder", null));
    }

    // ---- Settings ----

    [Fact]
    public void Settings_HasThePersonalRemindersSwitch_AndTheFormSendsIt()
    {
        var settings = Source("Pages", "Settings.razor");
        Assert.Contains("Personal reminders", settings);
        Assert.Contains("@bind=\"preferences.UserReminders\"", settings);

        var form = NotificationPreferencesForm.From(new NotificationPreferencesResponse(true, true, "22:00", "08:00", false));
        Assert.False(form.UserReminders);

        form.UserReminders = true;
        Assert.Equal(new SetNotificationPreferencesRequest(true, true, "22:00", "08:00", true), form.ToRequest());
    }

    // ---- Draft ----

    [Fact]
    public void NewDraft_IsOnceAtTheNextFullHour()
    {
        var draft = ReminderDraft.ForNew(LocalNow);
        Assert.Equal((ReminderDraft.Once, Today, "10:00", false), (draft.Repeat, draft.Date, draft.Time, draft.IsEditing));

        var lateEvening = ReminderDraft.ForNew(new DateTime(2026, 10, 14, 23, 40, 0));
        Assert.Equal((Today.AddDays(1), "00:00"), (lateEvening.Date, lateEvening.Time));
    }

    [Fact]
    public void Draft_Validates_WithTheApisLimits()
    {
        var draft = ReminderDraft.ForNew(LocalNow);
        Assert.Equal(["Say what to remind you about."], draft.Validate(LocalNow));

        draft.Title = new string('a', 81);
        draft.Note = new string('n', 501);
        draft.Time = "";
        Assert.Equal(["The title must be at most 80 characters.", "The note must be at most 500 characters.", "Choose a time."], draft.Validate(LocalNow));

        draft.Title = "Call the dentist";
        draft.Note = null;
        draft.Time = "09:20";
        Assert.Equal(["Choose a date and time in the future."], draft.Validate(LocalNow));

        draft.Time = "09:21:00";
        Assert.Empty(draft.Validate(LocalNow));

        draft.Repeat = ReminderDraft.Weekly;
        draft.ToggleWeekday(LocalNow.DayOfWeek);
        Assert.Equal(["Choose at least one day."], draft.Validate(LocalNow));

        // A past time is fine for repeating reminders: the next one is tomorrow.
        draft.Repeat = ReminderDraft.Daily;
        draft.Time = "07:00";
        Assert.Empty(draft.Validate(LocalNow));
    }

    [Fact]
    public void Draft_BuildsTheRequests()
    {
        var draft = ReminderDraft.ForNew(LocalNow);
        draft.Title = "  Water the plants ";
        draft.Note = "   ";
        draft.Repeat = ReminderDraft.Weekly;
        draft.ToggleWeekday(DayOfWeek.Thursday);
        draft.ToggleWeekday(DayOfWeek.Monday);
        draft.Time = "19:00:00";

        var create = draft.ToCreateRequest(120);
        Assert.Equal(("Custom", "Water the plants", (string?)null, 120), (create.Kind, create.Title, create.Note, create.UtcOffsetMinutes));
        // Wednesday (from ForNew), Monday and Thursday, in week order.
        Assert.Equal(["Monday", "Wednesday", "Thursday"], create.Schedule!.Weekdays!);
        Assert.Equal(("Weekly", (DateOnly?)null, "19:00"), (create.Schedule.Kind, create.Schedule.Date, create.Schedule.Time));

        draft.Repeat = ReminderDraft.Once;
        draft.SetDateFromPicker("2026-10-20");
        draft.SetDateFromPicker("garbage");
        var update = draft.ToUpdateRequest(-240);
        Assert.Equal(new ReminderScheduleRequest("Once", new DateOnly(2026, 10, 20), null, "19:00"), update.Schedule);
        Assert.Equal(-240, update.UtcOffsetMinutes);
    }

    [Fact]
    public void Draft_FromAResponse_KeepsEveryField()
    {
        var weekly = Reminder(new ReminderScheduleResponse("Weekly", null, ["Monday", "Thursday"], "19:00")) with { Note = "Balcony" };

        var draft = ReminderDraft.From(weekly, Today);

        Assert.Equal(("Take vitamins", "Balcony", ReminderDraft.Weekly, "19:00", true), (draft.Title, draft.Note, draft.Repeat, draft.Time, draft.IsEditing));
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Thursday], draft.SelectedWeekdays.Order());
        Assert.Equal(new DateOnly(2026, 10, 20), ReminderDraft.From(Reminder(new ReminderScheduleResponse("Once", new DateOnly(2026, 10, 20), null, "10:00")), Today).Date);
    }

    // ---- Display ----

    [Fact]
    public void ScheduleAndNext_AreReadable()
    {
        Assert.Equal("08:30 · Every day", ReminderDisplay.ScheduleText(new ReminderScheduleResponse("Daily", null, null, "08:30"), Today));
        Assert.Equal("19:00 · Mon, Thu", ReminderDisplay.ScheduleText(new ReminderScheduleResponse("Weekly", null, ["Thursday", "Monday"], "19:00"), Today));
        Assert.Equal("07:00 · Every day", ReminderDisplay.ScheduleText(new ReminderScheduleResponse("Weekly", null, [.. ReminderDisplay.Week.Select(d => d.ToString())], "07:00"), Today));
        Assert.Equal("Once on Tue 20 Oct at 10:00", ReminderDisplay.ScheduleText(new ReminderScheduleResponse("Once", new DateOnly(2026, 10, 20), null, "10:00"), Today));
        Assert.Equal("Once on Wed 13 Oct 2027 at 10:00", ReminderDisplay.ScheduleText(new ReminderScheduleResponse("Once", new DateOnly(2027, 10, 13), null, "10:00"), Today));

        Assert.Equal("Next: tomorrow 08:30", ReminderDisplay.NextText(Reminder(), Today));
        Assert.Equal("Next: today 21:00", ReminderDisplay.NextText(Reminder(next: "2026-10-14T21:00"), Today));
        Assert.Equal("Next: Tue 20 Oct 10:00", ReminderDisplay.NextText(Reminder(next: "2026-10-20T10:00"), Today));
        Assert.Null(ReminderDisplay.NextText(Reminder(status: "Completed", next: null), Today));
    }

    [Fact]
    public void LastOutcomeAndSnooze_AreLocalAndNeutral()
    {
        var rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
        var at = new DateTimeOffset(2026, 10, 14, 6, 30, 0, TimeSpan.Zero);

        Assert.Equal("Last: sent today 08:30", ReminderDisplay.LastText(Reminder(lastOutcome: "Notified", lastAt: at), Today, rome));
        Assert.Equal("Last: muted today 08:30", ReminderDisplay.LastText(Reminder(lastOutcome: "Disabled", lastAt: at), Today, rome));
        Assert.Equal("Last: missed", ReminderDisplay.LastText(Reminder(lastOutcome: "Missed", lastAt: at.AddDays(-3)), Today, rome));
        Assert.Null(ReminderDisplay.LastText(Reminder(), Today, rome));

        Assert.Equal("Snoozed until today 09:45", ReminderDisplay.SnoozeText(Reminder(snooze: new DateTimeOffset(2026, 10, 14, 7, 45, 0, TimeSpan.Zero)), Today, rome));
        Assert.Null(ReminderDisplay.SnoozeText(Reminder(), Today, rome));
    }

    [Fact]
    public void RowLabel_AnnouncesNameScheduleAndNext()
    {
        Assert.Equal("Take vitamins, every day at 08:30, next tomorrow 08:30", ReminderDisplay.RowLabel(Reminder(), Today));
        Assert.Equal("Take vitamins, Mon, Thu at 19:00, next tomorrow 08:30, paused",
            ReminderDisplay.RowLabel(Reminder(new ReminderScheduleResponse("Weekly", null, ["Monday", "Thursday"], "19:00"), status: "Paused"), Today));
        Assert.Equal("Take vitamins, once, Tue 20 Oct at 10:00",
            ReminderDisplay.RowLabel(Reminder(new ReminderScheduleResponse("Once", new DateOnly(2026, 10, 20), null, "10:00"), next: null), Today));
    }

    [Theory]
    [InlineData("23:00", "22:00", "08:00", true)]
    [InlineData("07:59", "22:00", "08:00", true)]
    [InlineData("08:00", "22:00", "08:00", false)]
    [InlineData("21:59", "22:00", "08:00", false)]
    [InlineData("13:30", "13:00", "14:00", true)]
    [InlineData("14:00", "13:00", "14:00", false)]
    [InlineData("10:00", null, null, false)]
    [InlineData("10:00", "10:00", "10:00", false)]
    [InlineData("", "22:00", "08:00", false)]
    public void QuietHours_FollowTheServersRule(string time, string? start, string? end, bool inside) =>
        Assert.Equal(inside, ReminderDisplay.IsInsideQuietHours(time, start, end));

    [Fact]
    public void QuietHoursWarning_IsText()
    {
        Assert.Equal("23:00 is inside your quiet hours (22:00–08:00). It will be delivered when they end.",
            ReminderDisplay.QuietHoursWarning("23:00:00", "22:00", "08:00"));
        Assert.Null(ReminderDisplay.QuietHoursWarning("12:00", "22:00", "08:00"));
    }

    // ---- API client ----

    [Fact]
    public async Task Client_UsesThePaths_TheOffset_AndReadableErrors()
    {
        var requests = new List<(HttpMethod Method, string Path, string? Body)>();
        var client = new RemindersApiClient(Http(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content?.ReadAsStringAsync().Result));

            return request.RequestUri!.AbsolutePath.EndsWith("/snooze", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = JsonContent.Create(new { title = "The reminder can't be changed this way.", detail = "Resume the reminder before snoozing it.", status = 409, code = "reminder_paused" })
                }
                : Json(Reminder());
        }), () => 120);

        Assert.True((await client.GetAsync(Id)).IsSuccess);
        Assert.True((await client.PauseAsync(Id)).IsSuccess);
        Assert.True((await client.ResumeAsync(Id)).IsSuccess);
        var snooze = await client.SnoozeAsync(Id, 60);

        Assert.Equal(["Resume the reminder before snoozing it."], snooze.Errors);
        Assert.Equal(
            [
                (HttpMethod.Get, $"/api/reminders/{Id}", (string?)null),
                (HttpMethod.Post, $"/api/reminders/{Id}/pause", null),
                (HttpMethod.Post, $"/api/reminders/{Id}/resume", "{\"utcOffsetMinutes\":120}"),
                (HttpMethod.Post, $"/api/reminders/{Id}/snooze", "{\"minutes\":60}")
            ],
            requests);
    }

    [Fact]
    public async Task Client_MapsNotFound_AndDeleteOfAGoneReminderSucceeds()
    {
        var client = new RemindersApiClient(Http(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), () => 0);

        Assert.True(RemindersApiClient.IsNotFound(await client.GetAsync(Id)));
        Assert.True((await client.DeleteAsync(Id)).IsSuccess);
    }

    // ---- Helpers ----

    private static string Page(string fileName) => Source("Pages", Path.Combine("Reminders", fileName));

    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new StubHandler(request => Task.FromResult(send(request)))) { BaseAddress = new Uri("https://lifeos.test/") };

    private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value) };

    private static string ComponentsRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components"));

    private static string Source(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(ComponentsRoot(), folder, fileName));

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
