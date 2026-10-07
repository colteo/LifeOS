using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LifeOS.App.Services.Notifications;
using LifeOS.App.Services.WeeklyReviews;
using LifeOS.Contracts.WeeklyReviews;

namespace LifeOS.UnitTests.App;

// AUTO-002 app: the Weekly Review entry in Modules (not the dock), the list and detail pages, the
// notification tap route, the API client and the display rules. Razor is checked by source, as in
// UiConsolidationTests.
public class WeeklyReviewAppTests
{
    // ---- Navigation ----

    [Fact]
    public void Modules_ListsWeeklyReview_AndTheDockIsUnchanged()
    {
        var more = Source("Pages", "More.razor");

        Assert.Contains("new(\"Weekly Review\", \"Your Sunday summary of the week\", \"history\", \"weekly-reviews\")", more);
        Assert.DoesNotContain("weekly", Source("Layout", "BottomDock.razor"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("weekly", Source("Layout", "AppHeader.razor"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pages_AreRouted_AndTheDetailMatchesTheTapPath()
    {
        var id = Guid.CreateVersion7();

        Assert.StartsWith("@page \"/weekly-reviews\"", Source("Pages", Path.Combine("WeeklyReviews", "WeeklyReviews.razor")));
        Assert.StartsWith("@page \"/weekly-reviews/{ReviewId:guid}\"", Detail());
        Assert.Equal($"weekly-reviews/{id:D}", NotificationTap.PathFor(new NotificationTarget(NotificationTargetKind.WeeklyReview, id)));
    }

    [Fact]
    public void TheTapIsOpenedByTheExistingAuthGateMachinery()
    {
        var gate = File.ReadAllText(Path.Combine(ComponentsRoot(), "Auth", "AuthGate.razor"));

        Assert.Contains("NotificationTap.PathFor(target) is { } path", gate);
        Assert.Contains("Navigation.NavigateTo(path);", gate);
    }

    // ---- List page ----

    [Fact]
    public void List_HasLoadingErrorEmptyAndPagingStates()
    {
        var list = List();

        Assert.Contains("Loading weekly reviews...", list);
        Assert.Contains("Retry", list);
        Assert.Contains("No weekly reviews yet.", list);
        Assert.Contains("Load more", list);
        Assert.Contains("href=\"@($\"weekly-reviews/{item.Id}\")\"", list);
        Assert.Contains("<PageHeader Title=\"@WeeklyReviewDisplay.Title\" BackHref=\"more\" />", list);
    }

    [Fact]
    public void List_ManagesTheEnabledSetting()
    {
        var list = List();

        Assert.Contains("Automatic weekly review", list);
        Assert.Contains("role=\"switch\"", list);
        Assert.Contains("ReviewsApi.SetEnabledAsync(requested)", list);
        Assert.Contains("ReviewsApi.GetSettingsAsync()", list);
    }

    // ---- Detail page ----

    [Fact]
    public void Detail_RendersTheSavedSnapshot_WithLoadingErrorAndEmptySections()
    {
        var detail = Detail();

        Assert.Contains("Loading weekly review...", detail);
        Assert.Contains("Retry", detail);
        Assert.Contains("ReviewsApi.GetAsync(ReviewId)", detail);
        Assert.Contains("No expenses or income this week.", detail);
        Assert.Contains("No completed workouts this week.", detail);
        Assert.Contains("No meals logged this week.", detail);
        Assert.Contains("Analyzed meals (partial)", detail);
        Assert.Contains("<PageHeader Title=\"@WeeklyReviewDisplay.Title\" BackHref=\"weekly-reviews\" />", detail);
    }

    // PD-9 kept for the deterministic review: no AI in the list, and none in the detail page itself.
    // AI-001 adds AI only through the separate AI Insights component, placed after every deterministic
    // section and given only the review id (never the figures).
    [Fact]
    public void AiAppearsOnlyInTheSeparateInsightsSection_AfterTheDeterministicReview()
    {
        var noAi = new Regex(@"\bAI\b|commentary|insight|estimat|AnalyzeDay", RegexOptions.IgnoreCase);
        var detail = Detail();
        var markup = Regex.Replace(detail.Replace("<WeeklyReviewInsightsSection ReviewId=\"@review.Id\" />", ""), @"@\*.*?\*@", "", RegexOptions.Singleline);

        Assert.DoesNotMatch(noAi, List());
        Assert.DoesNotMatch(noAi, markup);
        Assert.Single(Regex.Matches(detail, "<WeeklyReviewInsightsSection "));
        Assert.True(detail.IndexOf("<WeeklyReviewInsightsSection", StringComparison.Ordinal)
            > detail.LastIndexOf("No meals logged this week.", StringComparison.Ordinal));
    }

    [Fact]
    public void InsightsSection_HasEveryState_AndIsLabelledAsAi()
    {
        var section = Source("WeeklyReviews", "WeeklyReviewInsightsSection.razor");
        var css = Source("WeeklyReviews", "WeeklyReviewInsightsSection.razor.css");

        Assert.Contains("@WeeklyReviewDisplay.InsightsTitle", section);
        Assert.Contains(">AI</span>", section);
        foreach (var state in new[] { "Checking", "NotGenerated", "Generating", "Available" })
        {
            Assert.Contains($"case WeeklyReviewInsightsState.{state}", section);
        }

        Assert.Contains("Generate insights", section);
        Assert.Contains("Retry", section);
        Assert.Contains("@WeeklyReviewDisplay.InsightsDisclaimer", section);
        Assert.Contains("@WeeklyReviewDisplay.InsightsAttribution(insights)", section);
        Assert.Contains("border: 1px dashed", css);

        // Opening the page only reads; generation happens only when the user asks.
        Assert.Contains("panel.LoadAsync(ReviewId)", section);
        Assert.Contains("@onclick=\"GenerateAsync\"", section);
    }

    // ---- Display ----

    [Theory]
    [InlineData("2026-09-28", "2026-10-04", "28 Sep – 4 Oct 2026")]
    [InlineData("2026-06-01", "2026-06-07", "1 – 7 Jun 2026")]
    [InlineData("2025-12-29", "2026-01-04", "29 Dec 2025 – 4 Jan 2026")]
    public void WeekRange_IsDeterministic(string start, string end, string expected) =>
        Assert.Equal(expected, WeeklyReviewDisplay.WeekRange(DateOnly.Parse(start), DateOnly.Parse(end)));

    [Fact]
    public void Display_Wording()
    {
        Assert.Equal("Mon 28 Sep", WeeklyReviewDisplay.Day(new DateOnly(2026, 9, 28)));
        Assert.Equal("1 h 05 min", WeeklyReviewDisplay.Duration(3900));
        Assert.Equal("< 1 min", WeeklyReviewDisplay.Duration(-5));
        Assert.Equal(("1 workout", "2 workouts"), (WeeklyReviewDisplay.Workouts(1), WeeklyReviewDisplay.Workouts(2)));
        Assert.Equal("5 of 6 sets", WeeklyReviewDisplay.Sets(5, 6));
        Assert.Equal("3 of 7 days with meals", WeeklyReviewDisplay.DaysWithMeals(3));
        Assert.Equal("No meals logged", WeeklyReviewDisplay.MealCoverage(0, 0));
        Assert.Equal("1 meal, analyzed", WeeklyReviewDisplay.MealCoverage(1, 1));
        Assert.Equal("All 4 meals analyzed", WeeklyReviewDisplay.MealCoverage(4, 4));
        Assert.Equal("2 of 7 meals analyzed", WeeklyReviewDisplay.MealCoverage(2, 7));
        Assert.False(WeeklyReviewDisplay.ShowsNutritionTotals(0));
        Assert.True(WeeklyReviewDisplay.IsPartial(2, 7));
        Assert.False(WeeklyReviewDisplay.IsPartial(7, 7));
    }

    // ---- API client ----

    [Fact]
    public async Task Client_ReadsPagesDetailAndSettings_OnTheApiPaths()
    {
        var requests = new List<string>();
        var id = Guid.CreateVersion7();
        var client = new WeeklyReviewsApiClient(Http(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");

            return request.RequestUri.AbsolutePath switch
            {
                "/api/weekly-reviews" => Json(new WeeklyReviewPageResponse([new WeeklyReviewListItemResponse(id, new DateOnly(2026, 9, 28), new DateOnly(2026, 10, 4), DateTimeOffset.UnixEpoch)], "2026-10-04")),
                "/api/weekly-reviews/settings" when request.Method == HttpMethod.Get => Json(new WeeklyReviewSettingsResponse(true)),
                "/api/weekly-reviews/settings" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }));

        var page = await client.GetPageAsync(null);
        await client.GetPageAsync("2026-10-04");
        var settings = await client.GetSettingsAsync();
        var set = await client.SetEnabledAsync(false);
        var missing = await client.GetAsync(id);

        Assert.Equal(id, Assert.Single(page.Value!.Items).Id);
        Assert.True(settings.Value!.Enabled);
        Assert.True(set.IsSuccess);
        Assert.False(missing.IsSuccess);
        Assert.Equal(
            ["GET /api/weekly-reviews", "GET /api/weekly-reviews?cursor=2026-10-04", "GET /api/weekly-reviews/settings", "PUT /api/weekly-reviews/settings", $"GET /api/weekly-reviews/{id}"],
            requests);
    }

    [Fact]
    public void Client_IsRegisteredOnTheAuthorizedPipeline()
    {
        var program = File.ReadAllText(Path.Combine(ComponentsRoot(), "..", "MauiProgram.cs"));

        // AI-001: the second client (longer AI timeout) is used only to generate insights.
        Assert.Matches(new Regex(@"new LifeOS\.App\.Services\.WeeklyReviews\.WeeklyReviewsApiClient\(\s*CreateAuthorizedHttpClient\(services\), CreateAuthorizedHttpClient\(services, ApiTimeouts\.NutritionAi\)\)"), program);
    }

    // ---- Helpers ----

    private static string List() => Source("Pages", Path.Combine("WeeklyReviews", "WeeklyReviews.razor"));

    private static string Detail() => Source("Pages", Path.Combine("WeeklyReviews", "WeeklyReviewDetail.razor"));

    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> send) =>
        new(new StubHandler(request => Task.FromResult(send(request)))) { BaseAddress = new Uri("https://lifeos.test/") };

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static string ComponentsRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components"));

    private static string Source(string folder, string fileName) =>
        File.ReadAllText(Path.Combine(ComponentsRoot(), folder, fileName));

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
