using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LifeOS.Contracts.WeeklyReviews;
using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.IntegrationTests.Http;

// AUTO-002: GET /api/weekly-reviews, GET /api/weekly-reviews/{id} and the enabled setting, through
// the real API pipeline (routing, JWT, authorization) with the in-memory weekly review repository.
public class WeeklyReviewHttpTests
{
    private static readonly Guid UserA = Guid.CreateVersion7();
    private static readonly Guid UserB = Guid.CreateVersion7();
    private static readonly DateOnly LatestWeekEnd = new(2026, 10, 4);
    private static readonly DateTimeOffset GeneratedAt = new(2026, 10, 4, 18, 0, 3, TimeSpan.Zero);

    // ---- Authentication ----

    [Theory]
    [InlineData("GET", "/api/weekly-reviews")]
    [InlineData("GET", "/api/weekly-reviews/0192f0c3-0000-7000-8000-000000000001")]
    [InlineData("GET", "/api/weekly-reviews/settings")]
    [InlineData("PUT", "/api/weekly-reviews/settings")]
    public async Task EveryEndpoint_RequiresAUserAccessToken(string method, string path)
    {
        await using var factory = new LifeOSApiFactory();

        var response = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "PUT" ? JsonContent.Create(new { enabled = false }) : null
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- List ----

    [Fact]
    public async Task List_IsTheCallersReviews_NewestWeekFirst_InBoundedPages()
    {
        await using var factory = new LifeOSApiFactory();
        var weeks = Enumerable.Range(0, 5).Select(index => AddReview(factory, UserA, LatestWeekEnd.AddDays(-7 * index))).ToList();
        AddReview(factory, UserB, LatestWeekEnd);
        var client = Client(factory, UserA);

        var first = await client.GetFromJsonAsync<WeeklyReviewPageResponse>("/api/weekly-reviews?limit=2");
        var second = await client.GetFromJsonAsync<WeeklyReviewPageResponse>($"/api/weekly-reviews?limit=2&cursor={first!.NextCursor}");
        var last = await client.GetFromJsonAsync<WeeklyReviewPageResponse>($"/api/weekly-reviews?limit=2&cursor={second!.NextCursor}");

        Assert.Equal([weeks[0].Id, weeks[1].Id], first.Items.Select(item => item.Id));
        Assert.Equal("2026-09-27", first.NextCursor);
        Assert.Equal([weeks[2].Id, weeks[3].Id], second.Items.Select(item => item.Id));
        Assert.Equal([weeks[4].Id], last!.Items.Select(item => item.Id));
        Assert.Null(last.NextCursor);
        Assert.Equal((new DateOnly(2026, 9, 28), LatestWeekEnd, GeneratedAt), (first.Items[0].WeekStartDate, first.Items[0].WeekEndDate, first.Items[0].GeneratedAtUtc));
    }

    [Fact]
    public async Task List_ShowsNoSnapshotOrExecutionMetadata()
    {
        await using var factory = new LifeOSApiFactory();
        AddReview(factory, UserA, LatestWeekEnd);

        using var body = JsonDocument.Parse(await Client(factory, UserA).GetStringAsync("/api/weekly-reviews"));

        Assert.Equal(["items", "nextCursor"], body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["id", "weekStartDate", "weekEndDate", "generatedAtUtc"],
            body.RootElement.GetProperty("items")[0].EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task List_Empty_IsAnEmptyPage()
    {
        await using var factory = new LifeOSApiFactory();

        var page = await Client(factory, UserA).GetFromJsonAsync<WeeklyReviewPageResponse>("/api/weekly-reviews");

        Assert.Empty(page!.Items);
        Assert.Null(page.NextCursor);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=51")]
    [InlineData("?cursor=yesterday")]
    [InlineData("?cursor=2026-13-01")]
    public async Task List_RejectsAnInvalidLimitOrCursor(string query)
    {
        await using var factory = new LifeOSApiFactory();

        var response = await Client(factory, UserA).GetAsync($"/api/weekly-reviews{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Detail ----

    [Fact]
    public async Task Detail_IsTheSavedSnapshot()
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA, LatestWeekEnd);

        var response = await Client(factory, UserA).GetFromJsonAsync<WeeklyReviewResponse>($"/api/weekly-reviews/{review.Id}");

        Assert.Equal((review.Id, new DateOnly(2026, 9, 28), LatestWeekEnd, "Europe/Rome", GeneratedAt, 1),
            (response!.Id, response.WeekStartDate, response.WeekEndDate, response.TimeZoneId, response.GeneratedAtUtc, response.DataVersion));
        var eur = Assert.Single(response.Finance.Currencies);
        Assert.Equal(("EUR", 42.50m, 100m, 57.50m), (eur.Currency, eur.Expenses, eur.Income, eur.NetFlow));
        Assert.Equal([new WeeklyExpenseCategoryResponse("Food", 42.50m)], eur.ExpenseCategories);
        Assert.Equal((1, 3600L, 10, 12), (response.Gym.CompletedWorkouts, response.Gym.TotalDurationSeconds, response.Gym.CompletedSets, response.Gym.PrescribedSets));
        Assert.Equal(new WeeklyWorkoutResponse(new DateOnly(2026, 9, 29), "Upper", "Base", 3600, 10, 12), Assert.Single(response.Gym.Workouts));
        Assert.Equal((1, 2, 1, 0, 650.5m), (response.Nutrition.DaysWithMeals, response.Nutrition.MealCount, response.Nutrition.AnalyzedMealCount, response.Nutrition.FullyAnalyzedDays, response.Nutrition.AnalyzedCaloriesKcal));
    }

    [Fact]
    public async Task Detail_OfAnotherUsersReview_Is404()
    {
        await using var factory = new LifeOSApiFactory();
        var others = AddReview(factory, UserB, LatestWeekEnd);

        var response = await Client(factory, UserA).GetAsync($"/api/weekly-reviews/{others.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(others.Id.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Detail_OfAMissingReview_Is404()
    {
        await using var factory = new LifeOSApiFactory();

        Assert.Equal(HttpStatusCode.NotFound, (await Client(factory, UserA).GetAsync($"/api/weekly-reviews/{Guid.CreateVersion7()}")).StatusCode);
    }

    // ---- Enabled setting ----

    [Fact]
    public async Task Settings_AreEnabledByDefault_AndPerUser()
    {
        await using var factory = new LifeOSApiFactory();
        var clientA = Client(factory, UserA);

        Assert.True((await clientA.GetFromJsonAsync<WeeklyReviewSettingsResponse>("/api/weekly-reviews/settings"))!.Enabled);

        Assert.Equal(HttpStatusCode.NoContent, (await clientA.PutAsJsonAsync("/api/weekly-reviews/settings", new SetWeeklyReviewSettingsRequest(false))).StatusCode);

        Assert.False((await clientA.GetFromJsonAsync<WeeklyReviewSettingsResponse>("/api/weekly-reviews/settings"))!.Enabled);
        Assert.True((await Client(factory, UserB).GetFromJsonAsync<WeeklyReviewSettingsResponse>("/api/weekly-reviews/settings"))!.Enabled);
        Assert.False(factory.WeeklyReviews.Settings[UserA].Enabled);

        Assert.Equal(HttpStatusCode.NoContent, (await clientA.PutAsJsonAsync("/api/weekly-reviews/settings", new SetWeeklyReviewSettingsRequest(true))).StatusCode);
        Assert.True((await clientA.GetFromJsonAsync<WeeklyReviewSettingsResponse>("/api/weekly-reviews/settings"))!.Enabled);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"enabled\":null}")]
    public async Task Settings_RequireEnabled(string json)
    {
        await using var factory = new LifeOSApiFactory();

        var response = await Client(factory, UserA).PutAsync("/api/weekly-reviews/settings", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.WeeklyReviews.Settings);
    }

    [Fact]
    public async Task Settings_ForAMissingUser_Is404()
    {
        await using var factory = new LifeOSApiFactory();
        factory.WeeklyReviews.ExistingUsers = [];

        var response = await Client(factory, UserA).PutAsJsonAsync("/api/weekly-reviews/settings", new SetWeeklyReviewSettingsRequest(false));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Helpers ----

    private static HttpClient Client(LifeOSApiFactory factory, Guid userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.IssueAccessToken(userId));

        return client;
    }

    private static WeeklyReview AddReview(LifeOSApiFactory factory, Guid userId, DateOnly weekEnd)
    {
        var review = WeeklyReview.Create(userId, weekEnd, "Europe/Rome", GeneratedAt.AddDays((weekEnd.DayNumber - LatestWeekEnd.DayNumber)), new WeeklyReviewSnapshot(
            new WeeklyFinanceSummary([new WeeklyCurrencySummary("EUR", 42.50m, 100m, 57.50m, [new WeeklyExpenseCategory("Food", 42.50m)])]),
            new WeeklyGymSummary(1, 3600, 10, 12, [new WeeklyWorkout(weekEnd.AddDays(-5), "Upper", "Base", 3600, 10, 12)]),
            new WeeklyNutritionSummary(1, 2, 1, 0, 650.5m, 30m, 80m, 20m, [new WeeklyNutritionDay(weekEnd.AddDays(-6), 2, 1, 650.5m, 30m, 80m, 20m)])));
        factory.WeeklyReviews.Reviews.Add(review);

        return review;
    }
}
