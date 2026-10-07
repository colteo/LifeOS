using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.App.Services.ActionAgent;
using LifeOS.Contracts.ActionAgent;
using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.WeeklyReviews;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.TestHost;

namespace LifeOS.IntegrationTests.Http;

// AI-002: the suggested-action and proposal endpoints through the real API pipeline (routing, JWT,
// authorization, DI of the real handlers and read tools) with in-memory persistence and a scripted model
// port. Includes the app's API client.
public class ActionAgentHttpTests
{
    private static readonly Guid UserA = Guid.CreateVersion7();
    private static readonly Guid UserB = Guid.CreateVersion7();
    private static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");

    [Theory]
    [InlineData("GET", "/api/weekly-reviews/{0}/suggested-action")]
    [InlineData("POST", "/api/weekly-reviews/{0}/suggested-action")]
    [InlineData("GET", "/api/action-proposals/{0}")]
    [InlineData("POST", "/api/action-proposals/{0}/approve")]
    [InlineData("POST", "/api/action-proposals/{0}/reject")]
    public async Task EveryEndpoint_RequiresAUserAccessToken(string method, string path)
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA);

        var response = await factory.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), string.Format(path, review.Id)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.ActionAgentModel.Requests);
    }

    [Fact]
    public async Task Analyze_Proposes_ThenOnlyTheExplicitApprovalChangesTheBudget_Once()
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA);
        var (year, month) = CurrentMonth(factory);
        await SetBudgetAsync(factory, UserA, year, month, 400m);
        factory.ActionAgentModel.Script(FakeActionAgentModel.ReadReview(), FakeActionAgentModel.ReadBudget(year, month),
            FakeActionAgentModel.Propose(500m, year, month));
        var client = Client(factory, UserA);

        var before = await client.GetFromJsonAsync<SuggestedActionStateResponse>(ReviewPath(review.Id));
        var analyzed = await (await client.PostAsync(ReviewPath(review.Id), null)).Content.ReadFromJsonAsync<SuggestedActionStateResponse>();

        Assert.Equal(new SuggestedActionStateResponse("None", null, null), before);
        var proposal = analyzed!.Proposal!;
        Assert.Equal(("Proposal", "Pending", "MonthlyBudgetAdjustment"), (analyzed.Status, proposal.Status, proposal.ActionType));
        Assert.Equal(new MonthlyBudgetAdjustmentResponse(year, month, "EUR", 400m, 500m), proposal.Adjustment);
        Assert.Equal(400m, await BudgetAsync(factory, UserA, year, month));

        var approved = await client.PostAsync($"/api/action-proposals/{proposal.Id}/approve", null);
        var executed = await approved.Content.ReadFromJsonAsync<ProposedActionResponse>();

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("Executed", executed!.Status);
        Assert.NotNull(executed.ExecutedAtUtc);
        Assert.Equal(500m, await BudgetAsync(factory, UserA, year, month));

        // Replays change nothing: the user's later edit survives a repeated approval.
        await SetBudgetAsync(factory, UserA, year, month, 450m);
        var replay = await (await client.PostAsync($"/api/action-proposals/{proposal.Id}/approve", null)).Content.ReadFromJsonAsync<ProposedActionResponse>();
        Assert.Equal(("Executed", executed.ExecutedAtUtc), (replay!.Status, replay.ExecutedAtUtc));
        Assert.Equal(450m, await BudgetAsync(factory, UserA, year, month));

        var latest = await client.GetFromJsonAsync<SuggestedActionStateResponse>(ReviewPath(review.Id));
        Assert.Equal(("Proposal", "Executed"), (latest!.Status, latest.Proposal!.Status));
    }

    [Fact]
    public async Task ARejectedProposal_CannotBeApplied_409()
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA);
        var proposalId = await ProposeAsync(factory, review);
        var client = Client(factory, UserA);

        var rejected = await client.PostAsync($"/api/action-proposals/{proposalId}/reject", null);
        var approved = await client.PostAsync($"/api/action-proposals/{proposalId}/approve", null);

        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, approved.StatusCode);
        using var problem = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
        Assert.Equal("This suggestion was dismissed and can no longer be applied.", problem.RootElement.GetProperty("detail").GetString());
        var (year, month) = CurrentMonth(factory);
        Assert.Equal(400m, await BudgetAsync(factory, UserA, year, month));
    }

    [Fact]
    public async Task AnotherUsersReviewOrProposal_Is404_WithoutAnAiCall_OrAWrite()
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA);
        var proposalId = await ProposeAsync(factory, review);
        var calls = factory.ActionAgentModel.Requests.Count;
        var other = Client(factory, UserB);

        var responses = new[]
        {
            await other.GetAsync(ReviewPath(review.Id)),
            await other.PostAsync(ReviewPath(review.Id), null),
            await other.GetAsync($"/api/action-proposals/{proposalId}"),
            await other.PostAsync($"/api/action-proposals/{proposalId}/approve", null),
            await other.PostAsync($"/api/action-proposals/{proposalId}/reject", null),
            await other.PostAsync($"/api/action-proposals/{Guid.CreateVersion7()}/approve", null)
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Equal(calls, factory.ActionAgentModel.Requests.Count);
        Assert.Equal("Pending", factory.ProposedActions.Proposals.Single().Status.ToString());
        var (year, month) = CurrentMonth(factory);
        Assert.Equal(400m, await BudgetAsync(factory, UserA, year, month));
    }

    [Fact]
    public async Task NoAction_IsA200WithTheReason_AndStoresNothing()
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA);
        factory.ActionAgentModel.Script(FakeActionAgentModel.ReadReview(), FakeActionAgentModel.Finish("Your budget covers the month."));

        var response = await Client(factory, UserA).PostAsync(ReviewPath(review.Id), null);

        Assert.Equal(new SuggestedActionStateResponse("NoAction", null, "Your budget covers the month."),
            await response.Content.ReadFromJsonAsync<SuggestedActionStateResponse>());
        Assert.Empty(factory.ProposedActions.Proposals);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.ServiceUnavailable, "The assistant is unavailable right now. Try again later.")]
    [InlineData(true, HttpStatusCode.BadGateway, "The assistant could not suggest an action for this review. Try again later.")]
    public async Task AiFailures_AreStableProblems_AndNeverTouchFinance(bool invalidOutput, HttpStatusCode expected, string message)
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA);
        var (year, month) = CurrentMonth(factory);
        await SetBudgetAsync(factory, UserA, year, month, 400m);
        factory.ActionAgentModel.Script(FakeActionAgentModel.ReadBudget(year, month),
            invalidOutput ? FakeActionAgentModel.InvalidOutput : FakeActionAgentModel.Unavailable);

        var response = await Client(factory, UserA).PostAsync(ReviewPath(review.Id), null);

        Assert.Equal(expected, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(message, problem.RootElement.GetProperty("detail").GetString());
        Assert.False(problem.RootElement.TryGetProperty("exception", out _));
        Assert.Empty(factory.ProposedActions.Proposals);
        Assert.Equal(400m, await BudgetAsync(factory, UserA, year, month));
    }

    [Fact]
    public async Task TheProposal_HasTheDocumentedShape()
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA);
        var proposalId = await ProposeAsync(factory, review);

        using var body = JsonDocument.Parse(await Client(factory, UserA).GetStringAsync($"/api/action-proposals/{proposalId}"));

        Assert.Equal(
            ["id", "reviewId", "actionType", "status", "adjustment", "rationale", "createdAtUtc", "decidedAtUtc", "executedAtUtc", "failureCode",
             "provider", "model", "promptVersion"],
            body.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["year", "month", "currency", "currentAmount", "proposedAmount"],
            body.RootElement.GetProperty("adjustment").EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task WithTheRealClient_AndNoAiServiceConfigured_AnalysisIsUnavailable()
    {
        await using var factory = new LifeOSApiFactory(configure: builder =>
            builder.ConfigureTestServices(services =>
                services.Remove(services.Last(descriptor => descriptor.ServiceType == typeof(LifeOS.Application.ActionAgent.IActionAgentModel)))));
        var review = AddReview(factory, UserA);

        var response = await Client(factory, UserA).PostAsync(ReviewPath(review.Id), null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(factory.ProposedActions.Proposals);
    }

    [Fact]
    public async Task AppClient_AnalyzesApprovesAndReportsFailures()
    {
        await using var factory = new LifeOSApiFactory();
        var review = AddReview(factory, UserA);
        var (year, month) = CurrentMonth(factory);
        await SetBudgetAsync(factory, UserA, year, month, 400m);
        var api = new SuggestedActionApiClient(Client(factory, UserA));

        Assert.Equal("None", (await api.GetForReviewAsync(review.Id)).Value!.Status);

        factory.ActionAgentModel.Script(FakeActionAgentModel.Unavailable);
        Assert.Equal(["The assistant is unavailable right now. Try again later."], (await api.AnalyzeAsync(review.Id)).Errors);

        factory.ActionAgentModel.Script(FakeActionAgentModel.ReadBudget(year, month), FakeActionAgentModel.Propose(600m, year, month));
        var proposal = (await api.AnalyzeAsync(review.Id)).Value!.Proposal!;
        Assert.Equal("Executed", (await api.ApproveAsync(proposal.Id)).Value!.Status);
        Assert.Equal(["This suggestion was already approved and can no longer be dismissed."], (await api.RejectAsync(proposal.Id)).Errors);
        Assert.Equal(600m, await BudgetAsync(factory, UserA, year, month));
    }

    // ---- Helpers ----

    private static async Task<Guid> ProposeAsync(LifeOSApiFactory factory, WeeklyReview review)
    {
        var (year, month) = CurrentMonth(factory);
        await SetBudgetAsync(factory, review.UserId, year, month, 400m);
        factory.ActionAgentModel.Script(FakeActionAgentModel.ReadBudget(year, month), FakeActionAgentModel.Propose(500m, year, month));
        var analyzed = await (await Client(factory, review.UserId).PostAsync(ReviewPath(review.Id), null)).Content.ReadFromJsonAsync<SuggestedActionStateResponse>();

        return analyzed!.Proposal!.Id;
    }

    // The current local month in the review's zone (the only months a proposal may target are this one and the next).
    private static (int Year, int Month) CurrentMonth(LifeOSApiFactory factory)
    {
        var local = TimeZoneInfo.ConvertTime(factory.Clock.GetUtcNow(), Rome);

        return (local.Year, local.Month);
    }

    private static Task SetBudgetAsync(LifeOSApiFactory factory, Guid userId, int year, int month, decimal amount) =>
        factory.Budgets.SetAsync(MonthlyBudget.Create(userId, year, month, "EUR", amount), default);

    private static async Task<decimal?> BudgetAsync(LifeOSApiFactory factory, Guid userId, int year, int month) =>
        (await factory.Budgets.GetAsync(userId, year, month, "EUR", default))?.Amount;

    private static string ReviewPath(Guid reviewId) => $"/api/weekly-reviews/{reviewId}/suggested-action";

    private static HttpClient Client(LifeOSApiFactory factory, Guid userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.IssueAccessToken(userId));

        return client;
    }

    private static WeeklyReview AddReview(LifeOSApiFactory factory, Guid userId)
    {
        var weekEnd = new DateOnly(2026, 10, 4);
        var review = WeeklyReview.Create(userId, weekEnd, "Europe/Rome", new DateTimeOffset(2026, 10, 4, 18, 0, 3, TimeSpan.Zero), new WeeklyReviewSnapshot(
            new WeeklyFinanceSummary([new WeeklyCurrencySummary("EUR", 380m, 0m, -380m, [new WeeklyExpenseCategory("Groceries", 380m)])]),
            new WeeklyGymSummary(0, 0, 0, 0, []),
            new WeeklyNutritionSummary(0, 0, 0, 0, 0m, 0m, 0m, 0m, [])));
        factory.WeeklyReviews.Reviews.Add(review);

        return review;
    }
}
