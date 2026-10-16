using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LifeOS.App.Services;
using LifeOS.App.Services.Nutrition;
using LifeOS.Contracts.Nutrition.Hydration;

namespace LifeOS.UnitTests.App;

// HYD-001 H3 app: the Food diary's Water section, Targets → Water, the Home Water card, the API client
// (paths, errors) and the display rules. Razor is checked by source, as in HabitAppTests.
public class HydrationAppTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);
    private static readonly Guid Id = new("0198c0de-0000-7000-8000-000000000042");

    // ---- Formatting ----

    [Theory]
    [InlineData(1, "1 ml")]
    [InlineData(250, "250 ml")]
    [InlineData(999, "999 ml")]
    [InlineData(1000, "1 l")]
    [InlineData(1250, "1.25 l")]
    [InlineData(1500, "1.5 l")]
    [InlineData(10000, "10 l")]
    public void Amounts_AreMlBelow1000_AndLitresFrom1000(int ml, string text) =>
        Assert.Equal(text, HydrationFormat.Amount(ml));

    [Fact]
    public void Totals_CompareInMlWithATarget_AndShowOnlyTheTotalWithout()
    {
        Assert.Equal("1250 / 2000 ml", HydrationFormat.TotalLine(Day(1250, 2000), isToday: true));
        Assert.Equal("1.25 l today", HydrationFormat.TotalLine(Day(1250, null), isToday: true));
        Assert.Equal("1.25 l", HydrationFormat.TotalLine(Day(1250, null), isToday: false));
        Assert.Equal("0 ml today", HydrationFormat.TotalLine(Day(0, null), isToday: true));
    }

    [Fact]
    public void Progress_IsAFlooredPercent_TheBarIsCappedAtFull_AndNoTargetHasNeither()
    {
        Assert.Equal("62%", HydrationFormat.PercentText(Day(1250, 2000)));
        Assert.Equal("137%", HydrationFormat.PercentText(Day(2750, 2000)));
        Assert.Equal(100, HydrationFormat.BarPercent(Day(2750, 2000)));
        Assert.Equal(0, HydrationFormat.BarPercent(Day(0, 2000)));
        Assert.Null(HydrationFormat.PercentText(Day(1250, null)));
    }

    [Fact]
    public void Copy_StaysNeutral_AndIsAccessible()
    {
        Assert.Equal("Water today 1500 of 2000 millilitres", HydrationFormat.Announcement(Day(1500, 2000), isToday: true));
        Assert.Equal("Water on this day 1500 millilitres", HydrationFormat.Announcement(Day(1500, null), isToday: false));
        Assert.Equal("Add 250 millilitres of water", HydrationFormat.QuickAddAccessibleName(250));
        Assert.Equal("+ 250 ml", HydrationFormat.QuickAddLabel(250));
        Assert.Equal("Added 1.5 l", HydrationFormat.AddedMessage(1500));
        Assert.Equal("No water logged today.", HydrationFormat.EmptyMessage(Today, Today));
        Assert.Equal("No water logged on this day.", HydrationFormat.EmptyMessage(Today.AddDays(-1), Today));

        // P-9: over the target is fine; nothing warns, judges or advises.
        var copy = string.Join(" ", HydrationFormat.TotalLine(Day(10000, 2000), true), HydrationFormat.PercentText(Day(10000, 2000)),
            Source("Nutrition", "HydrationProgress.razor"), Source("Nutrition", "HydrationDiarySection.razor"), Source("Nutrition", "HomeHydrationCard.razor"));
        Assert.DoesNotMatch(new Regex(@"too much|warning|dehydrat|should drink|health|bg-danger", RegexOptions.IgnoreCase), copy);
    }

    [Fact]
    public void History_ListsSevenDaysEndingToday_WithTheSummary()
    {
        Assert.Equal((new DateOnly(2026, 10, 8), Today), HydrationFormat.HistoryRange(Today));
        Assert.Equal("Tue 13 · 1800 / 2000 ml", HydrationFormat.HistoryRow(Day(1800, 2000) with { Date = new DateOnly(2026, 10, 13) }));
        Assert.Equal("Sun 11 · 1.5 l", HydrationFormat.HistoryRow(Day(1500, null) with { Date = new DateOnly(2026, 10, 11) }));
        Assert.Equal("Met 4 of 6 days with a target · avg 1720 ml", HydrationFormat.HistorySummary(new HydrationRangeSummaryResponse(6, 4, 1720)));
        Assert.Equal("avg 900 ml", HydrationFormat.HistorySummary(new HydrationRangeSummaryResponse(0, 0, 900)));
    }

    [Fact]
    public void HomeCard_ShowsWithATargetOrWaterToday_Only()
    {
        Assert.False(HydrationFormat.ShowsHomeCard(Settings(null), Day(0, null)));
        Assert.True(HydrationFormat.ShowsHomeCard(Settings(2000), Day(0, 2000)));
        Assert.True(HydrationFormat.ShowsHomeCard(Settings(null), Day(250, null)));
    }

    [Fact]
    public void Requests_CarryTheDeviceOffsetForThatDateAndTime()
    {
        var rome = TimeZoneInfo.CreateCustomTimeZone("Test/Rome", TimeSpan.FromHours(2), "Rome", "Rome");

        Assert.Equal(new AddHydrationIntakeRequest(250, Today, new TimeOnly(8, 10), 120),
            HydrationFormat.AddRequest(250, Today, new TimeOnly(8, 10), rome));
        Assert.Equal(-300, HydrationFormat.CurrentUtcOffsetMinutes(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.FromHours(-5))));

        // A past day's quick add is at 12:00 (NUT-001); today's is now.
        Assert.Equal(new TimeOnly(12, 0), MealJournal.DefaultTime(Today.AddDays(-1), new DateTime(2026, 10, 14, 9, 41, 30)));
        Assert.Equal(new TimeOnly(9, 41), MealJournal.DefaultTime(Today, new DateTime(2026, 10, 14, 9, 41, 30)));
    }

    [Fact]
    public void Validation_MatchesTheApiBounds()
    {
        Assert.Null(HydrationFormat.AmountError(1));
        Assert.Null(HydrationFormat.AmountError(4000));
        Assert.NotNull(HydrationFormat.AmountError(0));
        Assert.NotNull(HydrationFormat.AmountError(4001));
        Assert.NotNull(HydrationFormat.AmountError(null));

        Assert.Null(HydrationFormat.TargetError(null));
        Assert.Null(HydrationFormat.TargetError(250));
        Assert.Null(HydrationFormat.TargetError(10000));
        Assert.NotNull(HydrationFormat.TargetError(249));
        Assert.NotNull(HydrationFormat.TargetError(10001));

        Assert.Empty(HydrationFormat.QuickAddErrors([200, 200, 750]));
        Assert.NotEmpty(HydrationFormat.QuickAddErrors([200, null, 750]));
        Assert.NotEmpty(HydrationFormat.QuickAddErrors([200, 330]));
    }

    [Fact]
    public void TargetNote_SaysChangesApplyFromToday_AndSinceWhen()
    {
        Assert.Equal("Changes apply from today; past days keep their target.", HydrationFormat.TargetNote(Settings(null), Today));
        Assert.Equal("2000 ml since 3 Oct 2026. Changes apply from today; past days keep their target.",
            HydrationFormat.TargetNote(Settings(2000) with { TargetEffectiveFrom = new DateOnly(2026, 10, 3) }, Today));
        Assert.Equal("No daily target since 3 Oct 2026. Changes apply from today; past days keep their target.",
            HydrationFormat.TargetNote(Settings(null) with { TargetEffectiveFrom = new DateOnly(2026, 10, 3) }, Today));
    }

    // ---- API client ----

    [Fact]
    public async Task Client_UsesTheHydrationRoutes()
    {
        var requests = new List<string>();
        var client = new NutritionApiClient(Http(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");

            return request.RequestUri!.AbsolutePath switch
            {
                "/api/nutrition/hydration/days/2026-10-14" => Json(new HydrationDayResponse(Day(250, null), [])),
                "/api/nutrition/hydration/days" => Json(new HydrationRangeResponse([], new HydrationRangeSummaryResponse(0, 0, 0))),
                "/api/nutrition/hydration/settings" or "/api/nutrition/hydration/target" or "/api/nutrition/hydration/quick-add" => Json(Settings(2000)),
                _ when request.Method == HttpMethod.Delete => Json(Day(0, null)),
                _ => Json(new HydrationIntakeMutationResponse(Intake(250), Day(250, null)), HttpStatusCode.Created)
            };
        }));

        Assert.True((await client.GetHydrationDayAsync(Today)).IsSuccess);
        Assert.True((await client.GetHydrationDaysAsync(Today.AddDays(-6), Today)).IsSuccess);
        Assert.Equal(250, (await client.AddHydrationAsync(new AddHydrationIntakeRequest(250, Today, new TimeOnly(8, 0), 120))).Value!.Intake.AmountMl);
        Assert.True((await client.UpdateHydrationAsync(Id, new UpdateHydrationIntakeRequest(300, new TimeOnly(8, 0)))).IsSuccess);
        Assert.Equal(0, (await client.DeleteHydrationAsync(Id)).Value!.TotalMl);
        Assert.Equal(2000, (await client.GetHydrationSettingsAsync(Today)).Value!.TargetMl);
        Assert.True((await client.SetHydrationTargetAsync(new SetHydrationTargetRequest(2000, 120))).IsSuccess);
        Assert.True((await client.SetHydrationQuickAddAsync(new SetHydrationQuickAddRequest([200, 330, 750], 120))).IsSuccess);

        Assert.Equal(
            [
                "GET /api/nutrition/hydration/days/2026-10-14",
                "GET /api/nutrition/hydration/days?from=2026-10-08&to=2026-10-14",
                "POST /api/nutrition/hydration/intakes",
                $"PUT /api/nutrition/hydration/intakes/{Id}",
                $"DELETE /api/nutrition/hydration/intakes/{Id}",
                "GET /api/nutrition/hydration/settings?date=2026-10-14",
                "PUT /api/nutrition/hydration/target",
                "PUT /api/nutrition/hydration/quick-add"
            ],
            requests);
    }

    [Fact]
    public async Task Client_ReportsMissingIntakes_ApiMessages_AndAnUnreachableApi()
    {
        var missing = new NutritionApiClient(Http(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Equal([NutritionApiClient.IntakeNotFoundMessage], (await missing.DeleteHydrationAsync(Id)).Errors);

        var invalid = new NutritionApiClient(Http(_ => Problem(HttpStatusCode.BadRequest,
            """{"title":"One or more validation errors occurred.","status":400,"errors":{"date":["Water can't be logged for a future day."]}}""")));
        Assert.Equal(["Water can't be logged for a future day."],
            (await invalid.AddHydrationAsync(new AddHydrationIntakeRequest(250, Today.AddDays(1), new TimeOnly(0, 5), 120))).Errors);

        var unreachable = new NutritionApiClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down")))
        {
            BaseAddress = new Uri("https://lifeos.test/")
        });
        Assert.False((await unreachable.GetHydrationDayAsync(Today)).IsSuccess);
    }

    // ---- Food diary ----

    [Fact]
    public void Diary_HasTheWaterSection_BelowTargetAboveMeals()
    {
        var diary = Source("Pages", Path.Combine("Nutrition", "Nutrition.razor"));
        var water = diary.IndexOf("<HydrationDiarySection Day=\"selectedDay\" Today=\"Today\" />", StringComparison.Ordinal);

        Assert.True(water > diary.IndexOf("@DayTarget()", StringComparison.Ordinal));
        Assert.True(water < diary.IndexOf("<h2 class=\"lo-section-title\">Meals</h2>", StringComparison.Ordinal));
    }

    [Fact]
    public void WaterSection_HasStates_QuickAddCustom_AndHistory()
    {
        var section = Source("Nutrition", "HydrationDiarySection.razor");

        Assert.Contains("Loading water...", section);
        Assert.Contains("Retry", section);
        Assert.Contains("<HydrationProgress Day=\"day.Day\" IsToday=\"@(Day == Today)\" />", section);
        Assert.Contains("@HydrationFormat.EmptyMessage(Day, Today)", section);
        Assert.Contains("<HydrationQuickAdd Amounts=\"quickAdd\" Day=\"Day\" Today=\"Today\"", section);
        Assert.Contains(">\n\t\t\t\tCustom\n\t\t\t</button>", section.Replace("\r\n", "\n"));
        Assert.Contains("formTime = MealJournal.DefaultTime(Day, DateTime.Now);", section);
        Assert.Contains("Last 7 days", section);
        Assert.Contains("HydrationFormat.HistoryRange(Day)", section);
        Assert.Contains("@HydrationFormat.HistorySummary(history.Summary)", section);
        // No reminders in v1 (REM-001 is separate).
        Assert.DoesNotMatch(new Regex(@"Remind me|HydrationGoal|href=""#""|TODO|Coming soon", RegexOptions.IgnoreCase), section);
    }

    [Fact]
    public void WaterSection_EditAndDelete_OnePanelAtATime_DeleteConfirmed()
    {
        var section = Source("Nutrition", "HydrationDiarySection.razor");

        Assert.Contains("aria-label=\"Actions for the @MealJournal.TimeText(intake.Time) water entry\"", section);
        Assert.Contains("var wasOpen = actionTarget?.Id == intake.Id;\n\t\tClose();", section.Replace("\r\n", "\n"));
        Assert.Contains(">Edit</button>", section);
        Assert.Contains("@IntakeForm(\"Save\", \"Saving...\", EditAsync, \"hydration-edit\")", section);
        Assert.Contains("NutritionApi.UpdateHydrationAsync(intake.Id, new UpdateHydrationIntakeRequest(amount, time)", section);
        Assert.Contains("water entry (@HydrationFormat.Amount(intake.AmountMl))?", section);
        Assert.Contains("@(isSaving ? \"Deleting...\" : \"Delete\")", section);
    }

    // ---- Quick add and Undo ----

    [Fact]
    public void QuickAdd_IsAccessible_DisabledInFlight_AndUndoable()
    {
        var quickAdd = Source("Nutrition", "HydrationQuickAdd.razor");
        var css = Source("Nutrition", "HydrationQuickAdd.razor.css");

        Assert.Contains("aria-label=\"@HydrationFormat.QuickAddAccessibleName(amount)\"", quickAdd);
        Assert.Contains("disabled=\"@(busy || Disabled)\"", quickAdd);
        Assert.Contains("MealJournal.DefaultTime(day, DateTime.Now)", quickAdd);
        Assert.Contains("role=\"status\"", quickAdd);
        Assert.Contains("@onfocusin=\"PauseUndo\" @onfocusout=\"ResumeUndo\"", quickAdd);
        Assert.Contains(">Undo</button>", quickAdd);
        Assert.Contains("NutritionApi.DeleteHydrationAsync(intake.Id, lifetime.Token)", quickAdd);
        Assert.Contains("await Task.Delay(HydrationFormat.UndoDuration, token);", quickAdd);
        Assert.Equal(TimeSpan.FromSeconds(6), HydrationFormat.UndoDuration);
        Assert.Contains("min-height: 3rem; /* ≥ 48dp touch targets */", css);
    }

    [Fact]
    public void Progress_IsAProgressbarInMl_WithATextEquivalent_AndALiveRegion()
    {
        var progress = Source("Nutrition", "HydrationProgress.razor");

        Assert.Contains("role=\"progressbar\"", progress);
        Assert.Contains("aria-valuemax=\"@target\" aria-valuenow=\"@Day.TotalMl\"", progress);
        Assert.Contains("aria-valuetext=\"@HydrationFormat.Announcement(Day, IsToday)\"", progress);
        Assert.Contains("aria-live=\"polite\"", progress);
        Assert.Contains("@HydrationFormat.TotalLine(Day, IsToday)", progress);
    }

    // ---- Targets ----

    [Fact]
    public void Targets_HaveAWaterSection_WithTargetAndQuickAdd()
    {
        Assert.Contains("<HydrationTargetsSection />", Source("Pages", Path.Combine("Nutrition", "NutritionTargets.razor")));

        var section = Source("Nutrition", "HydrationTargetsSection.razor");

        Assert.Contains("Daily target ml", section);
        Assert.Contains("placeholder=\"No daily target\"", section);
        Assert.Contains("@HydrationFormat.TargetNote(settings, Today)", section);
        Assert.Contains("Quick add ml", section);
        Assert.Contains("Save water settings", section);
        Assert.Contains("if (formTarget != current.TargetMl)", section);
        Assert.Contains("new SetHydrationTargetRequest(formTarget, offset)", section);
        Assert.Contains("new SetHydrationQuickAddRequest(amounts, offset)", section);
        // No date picker: the target always applies from today.
        Assert.DoesNotContain("type=\"date\"", section);
    }

    // ---- Home ----

    [Fact]
    public void Home_HasTheWaterCard_ShownOnlyForUsers_OpeningTheDiary()
    {
        Assert.Contains("<HomeHydrationCard />", Source("Pages", "Home.razor"));

        var card = Source("Nutrition", "HomeHydrationCard.razor");

        Assert.Contains("HydrationFormat.ShowsHomeCard(settings, day)", card);
        Assert.Contains("<a href=\"nutrition\" class=\"hydration-home__title\"", card);
        Assert.Contains("<HydrationQuickAdd Amounts=\"settings.QuickAddMl\" Day=\"today\" Today=\"today\"", card);
        Assert.Contains("<HydrationProgress Day=\"day\" IsToday=\"true\" />", card);
        Assert.Contains("<section class=\"lo-section\">", card);
    }

    // ---- Helpers ----

    private static HydrationDaySummaryResponse Day(int total, int? target) =>
        new(Today, total, target, target is { } t && total >= t, target is { } value ? total * 100 / value : null);

    private static HydrationSettingsResponse Settings(int? target) =>
        new(Today, target, target is null ? null : Today, [150, 250, 500]);

    private static HydrationIntakeResponse Intake(int amount) =>
        new(Id, amount, Today, new TimeOnly(8, 0), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

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
