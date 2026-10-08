using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using LifeOS.App.Services.ActionAgent;
using LifeOS.Contracts.ActionAgent;

namespace LifeOS.UnitTests.App;

// AI-002 app: the "Suggested action" card behind the Weekly Review detail page — its state model
// (checking, none, analysing, no action, proposal, deciding, error with retry), what it lets the user do
// in each proposal status, its wording, and (by source) that approval is an explicit tap.
public class SuggestedActionAppTests
{
    private static readonly Guid ReviewId = Guid.CreateVersion7();
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly ProposedActionResponse Pending = new(
        Guid.CreateVersion7(), ReviewId, "MonthlyBudgetAdjustment", ProposedActionStatuses.Pending,
        new MonthlyBudgetAdjustmentResponse(2026, 10, "EUR", 400m, 500m), "You spent 380 of 400 EUR.",
        new DateTimeOffset(2026, 10, 7, 10, 0, 0, TimeSpan.Zero), null, null, null, "groq", "openai/gpt-oss-20b", "action-agent-v1");

    [Fact]
    public async Task Load_None_ThenAnalyze_ShowsTheProposal_WhichAwaitsTheUsersDecision()
    {
        var api = new ScriptedApi(
            Ok(new SuggestedActionStateResponse(SuggestedActionStatuses.None, null, null)),
            Ok(new SuggestedActionStateResponse(SuggestedActionStatuses.Proposal, Pending, null)));
        var panel = new SuggestedActionPanel(api.Client);

        Assert.Equal(SuggestedActionState.Checking, panel.State);
        await panel.LoadAsync(ReviewId);
        Assert.Equal(SuggestedActionState.None, panel.State);
        Assert.True(panel.CanAnalyze);

        await panel.AnalyzeAsync();

        Assert.Equal(SuggestedActionState.Proposal, panel.State);
        Assert.Equal(Pending, panel.Proposal);
        Assert.Equal((true, true, false), (panel.CanApply, panel.CanReject, panel.CanAnalyze));
        Assert.Equal([$"GET {ReviewPath}", $"POST {ReviewPath}"], api.Requests);
    }

    [Fact]
    public async Task Apply_SendsTheApproval_AndShowsTheExecutedProposal()
    {
        var executed = Pending with { Status = ProposedActionStatuses.Executed, DecidedAtUtc = Pending.CreatedAtUtc, ExecutedAtUtc = Pending.CreatedAtUtc };
        var api = new ScriptedApi(Ok(new SuggestedActionStateResponse(SuggestedActionStatuses.Proposal, Pending, null)), Ok(executed));
        var panel = new SuggestedActionPanel(api.Client);
        await panel.LoadAsync(ReviewId);

        await panel.ApproveAsync();

        Assert.Equal((SuggestedActionState.Proposal, ProposedActionStatuses.Executed), (panel.State, panel.Proposal!.Status));
        Assert.Equal((false, false, true), (panel.CanApply, panel.CanReject, panel.CanAnalyze));
        Assert.Equal($"POST /api/action-proposals/{Pending.Id}/approve", api.Requests[^1]);
    }

    [Fact]
    public async Task NotNow_SendsTheRejection()
    {
        var api = new ScriptedApi(Ok(new SuggestedActionStateResponse(SuggestedActionStatuses.Proposal, Pending, null)),
            Ok(Pending with { Status = ProposedActionStatuses.Rejected, DecidedAtUtc = Pending.CreatedAtUtc }));
        var panel = new SuggestedActionPanel(api.Client);
        await panel.LoadAsync(ReviewId);

        await panel.RejectAsync();

        Assert.Equal(ProposedActionStatuses.Rejected, panel.Proposal!.Status);
        Assert.False(panel.CanApply);
        Assert.Equal($"POST /api/action-proposals/{Pending.Id}/reject", api.Requests[^1]);
    }

    [Fact]
    public async Task DecidedProposals_OfferNoDecision_AndNothingIsSent()
    {
        foreach (var status in new[] { ProposedActionStatuses.Executed, ProposedActionStatuses.Rejected, ProposedActionStatuses.Failed })
        {
            var api = new ScriptedApi(Ok(new SuggestedActionStateResponse(SuggestedActionStatuses.Proposal, Pending with { Status = status }, null)));
            var panel = new SuggestedActionPanel(api.Client);
            await panel.LoadAsync(ReviewId);

            await panel.ApproveAsync();
            await panel.RejectAsync();

            Assert.Equal((false, false), (panel.CanApply, panel.CanReject));
            Assert.Single(api.Requests);
        }
    }

    [Fact]
    public async Task AnApprovedProposal_CanOnlyBeApplied()
    {
        var api = new ScriptedApi(Ok(new SuggestedActionStateResponse(SuggestedActionStatuses.Proposal, Pending with { Status = ProposedActionStatuses.Approved }, null)));
        var panel = new SuggestedActionPanel(api.Client);

        await panel.LoadAsync(ReviewId);

        Assert.Equal((true, false), (panel.CanApply, panel.CanReject));
    }

    [Fact]
    public async Task NoAction_ShowsTheReason()
    {
        var api = new ScriptedApi(
            Ok(new SuggestedActionStateResponse(SuggestedActionStatuses.None, null, null)),
            Ok(new SuggestedActionStateResponse(SuggestedActionStatuses.NoAction, null, "Your budget covers the month.")));
        var panel = new SuggestedActionPanel(api.Client);
        await panel.LoadAsync(ReviewId);

        await panel.AnalyzeAsync();

        Assert.Equal((SuggestedActionState.NoAction, "Your budget covers the month."), (panel.State, panel.Message));
        Assert.True(panel.CanAnalyze);
    }

    [Fact]
    public async Task AFailedAnalysis_IsRetried_AFailedDecision_ReloadsInsteadOfResending()
    {
        var api = new ScriptedApi(
            Ok(new SuggestedActionStateResponse(SuggestedActionStatuses.None, null, null)),
            Problem(HttpStatusCode.ServiceUnavailable, "The assistant is unavailable right now. Try again later."),
            Ok(new SuggestedActionStateResponse(SuggestedActionStatuses.Proposal, Pending, null)),
            Problem(HttpStatusCode.Conflict, "This suggestion was dismissed and can no longer be applied."),
            Ok(new SuggestedActionStateResponse(SuggestedActionStatuses.Proposal, Pending with { Status = ProposedActionStatuses.Rejected }, null)));
        var panel = new SuggestedActionPanel(api.Client);
        await panel.LoadAsync(ReviewId);

        await panel.AnalyzeAsync();
        Assert.Equal((SuggestedActionState.Error, "The assistant is unavailable right now. Try again later."), (panel.State, panel.ErrorMessage));
        await panel.RetryAsync();
        Assert.Equal(SuggestedActionState.Proposal, panel.State);

        await panel.ApproveAsync();
        Assert.Equal((SuggestedActionState.Error, "This suggestion was dismissed and can no longer be applied."), (panel.State, panel.ErrorMessage));
        await panel.RetryAsync();

        Assert.Equal(ProposedActionStatuses.Rejected, panel.Proposal!.Status);
        Assert.Equal([$"GET {ReviewPath}", $"POST {ReviewPath}", $"POST {ReviewPath}", $"POST /api/action-proposals/{Pending.Id}/approve", $"GET {ReviewPath}"],
            api.Requests);
    }

    [Fact]
    public async Task WhileAnalyzing_ASecondRequestIsIgnored()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>();
        var api = new ScriptedApi(Ok(new SuggestedActionStateResponse(SuggestedActionStatuses.None, null, null)));
        var panel = new SuggestedActionPanel(api.Client);
        await panel.LoadAsync(ReviewId);
        api.Next = release.Task;

        var first = panel.AnalyzeAsync();
        Assert.True(panel.IsBusy);
        await panel.AnalyzeAsync();
        release.SetResult(Ok(new SuggestedActionStateResponse(SuggestedActionStatuses.NoAction, null, null)));
        await first;

        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task OnlyTheAnalysis_UsesTheAiTimeoutClient()
    {
        var normal = new ScriptedApi(Enumerable.Range(0, 3).Select(_ => Ok(Pending)).Prepend(Ok(new SuggestedActionStateResponse("None", null, null))).ToArray());
        var ai = new ScriptedApi(Ok(new SuggestedActionStateResponse("None", null, null)));
        var client = new SuggestedActionApiClient(new HttpClient(normal) { BaseAddress = new Uri("https://lifeos.test/") },
            new HttpClient(ai) { BaseAddress = new Uri("https://lifeos.test/") });

        await client.GetForReviewAsync(ReviewId);
        await client.AnalyzeAsync(ReviewId);
        await client.ApproveAsync(Pending.Id);
        await client.RejectAsync(Pending.Id);

        Assert.Equal([$"POST {ReviewPath}"], ai.Requests);
        Assert.Equal(3, normal.Requests.Count);
    }

    // ---- Display ----

    [Fact]
    public void TheCard_SaysExactlyWhatWillChange()
    {
        var adjustment = Pending.Adjustment;

        Assert.Equal("Change your October 2026 EUR budget", SuggestedActionDisplay.Heading(adjustment));
        Assert.Equal("400.00 EUR → 500.00 EUR", SuggestedActionDisplay.Change(adjustment, Invariant));
        Assert.Equal("+100.00 EUR", SuggestedActionDisplay.Difference(adjustment, Invariant));
        Assert.Equal("−50.00 EUR", SuggestedActionDisplay.Difference(adjustment with { ProposedAmount = 350m }, Invariant));
        Assert.Equal("Suggested by AI (groq · openai/gpt-oss-20b · action-agent-v1). You decide.", SuggestedActionDisplay.Attribution(Pending));
        Assert.Equal(SuggestedActionDisplay.NoActionFallback, SuggestedActionDisplay.NoAction(null));
    }

    [Fact]
    public void Outcomes_AreStatedPlainly()
    {
        Assert.Null(SuggestedActionDisplay.Outcome(Pending));
        Assert.Equal("Applied. The budget is now 500.00 EUR.",
            SuggestedActionDisplay.Outcome(Pending with { Status = ProposedActionStatuses.Executed }, Invariant));
        Assert.Equal("Dismissed. Nothing was changed.", SuggestedActionDisplay.Outcome(Pending with { Status = ProposedActionStatuses.Rejected }));
        Assert.Contains("the budget changed", SuggestedActionDisplay.Outcome(Pending with { Status = ProposedActionStatuses.Failed, FailureCode = "budget_changed" }));
    }

    // ---- Markup (by source) ----

    [Fact]
    public void TheSection_FollowsTheInsights_IsLabelledAsAi_AndChangesNothingWithoutATap()
    {
        var detail = Source(Path.Combine("Pages", "WeeklyReviews", "WeeklyReviewDetail.razor"));
        var section = Source(Path.Combine("WeeklyReviews", "SuggestedActionSection.razor"));
        var css = Source(Path.Combine("WeeklyReviews", "SuggestedActionSection.razor.css"));

        Assert.Single(Regex.Matches(detail, "<SuggestedActionSection "));
        Assert.True(detail.IndexOf("<SuggestedActionSection", StringComparison.Ordinal) > detail.IndexOf("<WeeklyReviewInsightsSection", StringComparison.Ordinal));
        Assert.Contains(">AI</span>", section);
        Assert.Contains("border: 1px dashed", css);
        foreach (var state in new[] { "Checking", "Analyzing", "None", "NoAction", "Proposal" })
        {
            Assert.Contains($"SuggestedActionState.{state}", section);
        }

        // Opening the page only reads; analysis and decisions happen only on a tap.
        Assert.Contains("panel.LoadAsync(ReviewId)", section);
        Assert.Contains("@onclick=\"AnalyzeAsync\"", section);
        Assert.Contains(">Apply</button>", section);
        Assert.Contains(">Not now</button>", section);
        Assert.Contains("@onclick=\"ApproveAsync\"", section);
        Assert.Contains("Current budget", section);
        Assert.Contains("Proposed budget", section);
        Assert.Contains("@proposal.Rationale", section);
        Assert.Matches(new Regex(@"new LifeOS\.App\.Services\.ActionAgent\.SuggestedActionApiClient\(\s*CreateAuthorizedHttpClient\(services\), CreateAuthorizedHttpClient\(services, ApiTimeouts\.NutritionAi\)\)"),
            File.ReadAllText(Path.Combine(ComponentsRoot(), "..", "MauiProgram.cs")));
    }

    private static string ReviewPath => $"/api/weekly-reviews/{ReviewId}/suggested-action";

    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static HttpResponseMessage Problem(HttpStatusCode status, string detail) =>
        new(status) { Content = new StringContent($$"""{"title":"x","detail":"{{detail}}","status":{{(int)status}}}""", Encoding.UTF8, "application/problem+json") };

    private static string ComponentsRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "..", "src", "dotnet", "LifeOS.App", "Components"));

    private static string Source(string relativePath) => File.ReadAllText(Path.Combine(ComponentsRoot(), relativePath));

    // Answers requests in order; Next, when set, answers the following request instead.
    private sealed class ScriptedApi : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public ScriptedApi(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
            Client = new SuggestedActionApiClient(new HttpClient(this) { BaseAddress = new Uri("https://lifeos.test/") });
        }

        public SuggestedActionApiClient Client { get; }

        public List<string> Requests { get; } = [];

        public Task<HttpResponseMessage>? Next { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");

            if (Next is { } next)
            {
                Next = null;
                return next;
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
