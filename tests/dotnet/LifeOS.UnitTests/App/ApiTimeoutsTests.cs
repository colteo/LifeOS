using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using LifeOS.App.Services;
using LifeOS.App.Services.Nutrition;
using LifeOS.Contracts.Nutrition;

namespace LifeOS.UnitTests.App;

// PROD-AI-001: only the Nutrition calls that reach the AI service get the longer, still finite, timeout;
// every other LifeOS API call keeps the default one.
public class ApiTimeoutsTests
{
    // Production chain (docs/operations/production-runbook.md): the API wakes in about a minute, then waits
    // up to NutritionAi__TimeoutSeconds = 120 for the AI service.
    private static readonly TimeSpan ApiWake = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProductionAiServiceTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    public void TheDefaultTimeout_IsUnchanged()
    {
        Assert.Equal(TimeSpan.FromSeconds(90), ApiTimeouts.Default);
    }

    [Fact]
    public void TheNutritionAiTimeout_CoversBothColdStarts_AndStaysFinite()
    {
        Assert.True(ApiTimeouts.NutritionAi > ApiWake + ProductionAiServiceTimeout);
        Assert.True(ApiTimeouts.NutritionAi <= TimeSpan.FromMinutes(4));
    }

    [Fact]
    public async Task OnlyEstimateAnalyzeAndLazyClose_UseTheAiClient()
    {
        var normal = new RecordingHandler();
        var ai = new RecordingHandler();
        var client = new NutritionApiClient(
            new HttpClient(normal) { BaseAddress = new Uri("https://api.example.test/") },
            new HttpClient(ai) { BaseAddress = new Uri("https://api.example.test/") });
        var day = new DateOnly(2026, 10, 3);

        await client.EstimateAsync(Guid.CreateVersion7());
        await client.AnalyzeDayAsync(day);
        await client.LazyCloseAsync(120);
        await client.GetMealsAsync(day);
        await client.GetSummaryAsync(day);
        await client.SetNutritionAsync(Guid.CreateVersion7(), new SetMealNutritionRequest(1, 1, 1, 1, "UserAdjusted"));
        await client.DeleteMealAsync(Guid.CreateVersion7());

        Assert.Equal(["estimate", "analyze", "lazy-close"], ai.Paths.Select(path => path.Split('/')[^1]));
        Assert.Equal(4, normal.Paths.Count);
        Assert.DoesNotContain(normal.Paths, path => path.EndsWith("/estimate") || path.EndsWith("/analyze") || path.EndsWith("/lazy-close"));
    }

    // AI-001: the only clients with the AI timeout are Nutrition's and Weekly Review's (insights), and
    // (AI-002) the suggested action's (analysis).
    [Fact]
    public void TheApp_GivesTheNutritionAndWeeklyReviewClientsTheAiTimeout()
    {
        var program = File.ReadAllText(Path.Combine(AppDirectory(), "MauiProgram.cs"));

        Assert.Contains("CreateAuthorizedHttpClient(services, ApiTimeouts.NutritionAi)", program);
        Assert.Contains("httpClient.Timeout = timeout ?? ApiTimeouts.Default;", program);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(program, "ApiTimeouts.NutritionAi").Count);
    }

    [Fact]
    public async Task OnlyGeneratingWeeklyReviewInsights_UsesTheAiClient()
    {
        var normal = new RecordingHandler();
        var ai = new RecordingHandler();
        var client = new LifeOS.App.Services.WeeklyReviews.WeeklyReviewsApiClient(
            new HttpClient(normal) { BaseAddress = new Uri("https://api.example.test/") },
            new HttpClient(ai) { BaseAddress = new Uri("https://api.example.test/") });
        var id = Guid.CreateVersion7();

        await client.GenerateInsightsAsync(id);
        await client.GetInsightsAsync(id);
        await client.GetAsync(id);
        await client.GetPageAsync(null);
        await client.GetSettingsAsync();

        Assert.Equal([$"/api/weekly-reviews/{id}/insights"], ai.Paths);
        Assert.Equal(4, normal.Paths.Count);
    }

    private static string AppDirectory([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App"));

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }
}
