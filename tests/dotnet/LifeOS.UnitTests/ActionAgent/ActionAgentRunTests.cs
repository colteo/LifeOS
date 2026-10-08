using System.Text.Json;
using LifeOS.Application.ActionAgent;
using LifeOS.Application.Finance.Budgets;
using LifeOS.Domain.ActionAgent;
using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.Finance.Transactions;
using LifeOS.Domain.WeeklyReviews;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.ActionAgent;

// AI-002: the Action Agent run — a bounded tool-use loop over a scripted model. Tool exposure, stop
// conditions (final answer, max steps, unknown tool, repeated call, invalid output), invalid arguments
// fed back, server-side proposal validation, no-action, and that a run never writes Finance data.
public class ActionAgentRunTests
{
    // Wednesday 7 October 2026, 12:00 in Rome: the current local month is October.
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly WeekEnd = new(2026, 10, 4);

    private readonly InMemoryWeeklyReviewRepository _reviews = new();
    private readonly InMemoryProposedActionRepository _proposals;
    private readonly InMemoryMonthlyBudgetRepository _budgets = new();
    private readonly InMemoryTransactionRepository _transactions = new();
    private readonly FakeActionAgentModel _model = new();
    private readonly FixedTimeProvider _clock = new(Now);

    public ActionAgentRunTests()
    {
        _proposals = new InMemoryProposedActionRepository(_reviews);
    }

    // ---- Happy path and tool exposure ----

    [Fact]
    public async Task TheAgentReadsThroughLifeOSTools_AndEndsWithAPendingProposal_WithoutWritingFinance()
    {
        var review = AddReview(TestUsers.A);
        await SetBudgetAsync(TestUsers.A, 400m);
        await SpendAsync(TestUsers.A, 380m);
        _model.Script(FakeActionAgentModel.ReadReview(), FakeActionAgentModel.ReadBudget(), FakeActionAgentModel.Propose(500m));

        var result = await Run(TestUsers.A, review.Id);

        Assert.Equal(ActionAgentRunStatus.Proposed, result.Status);
        var proposal = Assert.Single(_proposals.Proposals);
        Assert.Same(proposal, result.Proposal);
        Assert.Equal(ProposedActionStatus.Pending, proposal.Status);
        Assert.Equal(new MonthlyBudgetAdjustment(2026, 10, "EUR", 400m, 500m), proposal.Payload);
        Assert.Equal((TestUsers.A, review.Id), (proposal.UserId, proposal.ReviewId));
        Assert.Equal(("fake", "fake-model", "action-agent-v1", ActionAgentTools.ToolSchemaVersion, 3),
            (proposal.Run.Provider, proposal.Run.Model, proposal.Run.PromptVersion, proposal.Run.ToolSchemaVersion, proposal.Run.StepCount));
        Assert.Equal([ActionAgentTools.GetWeeklyReview, ActionAgentTools.GetBudgetStatus], proposal.Run.ToolCalls);

        // Approval required: the budget is untouched.
        Assert.Equal(400m, (await _budgets.GetAsync(TestUsers.A, 2026, 10, "EUR", default))!.Amount);
    }

    [Fact]
    public async Task EveryStep_OffersExactlyTheTwoReadOnlyTools_AndTheTaskFraming()
    {
        var review = AddReview(TestUsers.A);
        await SetBudgetAsync(TestUsers.A, 400m);
        _model.Script(FakeActionAgentModel.ReadReview(), FakeActionAgentModel.ReadBudget(), FakeActionAgentModel.Finish("Fine."));

        await Run(TestUsers.A, review.Id);

        Assert.Equal(3, _model.Requests.Count);
        foreach (var request in _model.Requests)
        {
            Assert.Equal(["get_weekly_review", "get_budget_status"], request.Tools.Select(tool => tool.Name));
            Assert.Equal(ActionAgentTools.ToolSchemaVersion, request.ToolSchemaVersion);
            Assert.Equal(RunActionAgentHandler.MaxSteps, request.MaxSteps);
            Assert.Equal("2026-10", request.Context.CurrentMonth);
            Assert.Equal(["2026-10", "2026-11"], request.Context.TargetMonths);
        }

        Assert.DoesNotContain(ActionAgentTools.Definitions, tool =>
            tool.Name.Contains("set", StringComparison.OrdinalIgnoreCase) || tool.Name.Contains("update", StringComparison.OrdinalIgnoreCase)
            || tool.Name.Contains("delete", StringComparison.OrdinalIgnoreCase) || tool.Name.Contains("apply", StringComparison.OrdinalIgnoreCase));
        Assert.Equal([0, 1, 2], _model.Requests.Select(request => request.Steps.Count));
    }

    [Fact]
    public async Task ToolResults_AreMinimal_AndContainNoIdentifiers()
    {
        var review = AddReview(TestUsers.A);
        await SetBudgetAsync(TestUsers.A, 400m);
        await SpendAsync(TestUsers.A, 380m);
        _model.Script(FakeActionAgentModel.ReadReview(), FakeActionAgentModel.ReadBudget(), FakeActionAgentModel.Finish("Fine."));

        await Run(TestUsers.A, review.Id);

        var steps = _model.Requests[^1].Steps;
        var weekly = steps[0].Result;
        Assert.Equal(["week_start", "week_end", "currencies"], weekly.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["currency", "expenses", "income", "net_flow", "top_expense_categories"],
            weekly.GetProperty("currencies")[0].EnumerateObject().Select(property => property.Name));
        var budget = steps[1].Result;
        Assert.True(budget.GetProperty("budget_set").GetBoolean());
        Assert.Equal((400m, 380m, 20m), (budget.GetProperty("amount").GetDecimal(), budget.GetProperty("spent").GetDecimal(),
            budget.GetProperty("remaining").GetDecimal()));

        var everything = JsonSerializer.Serialize(_model.Requests);
        Assert.DoesNotContain(TestUsers.A.ToString(), everything);
        Assert.DoesNotContain(review.Id.ToString(), everything);
        Assert.DoesNotContain(_budgetId.ToString(), everything);
        Assert.DoesNotContain("Europe", everything);
    }

    // ---- Stop conditions ----

    [Fact]
    public async Task TheRunStopsAtMaxSteps_ATheLastStepMustBeFinal()
    {
        var review = AddReview(TestUsers.A);
        _model.Script(
            FakeActionAgentModel.ReadReview(),
            FakeActionAgentModel.ReadBudget(month: 10),
            FakeActionAgentModel.ReadBudget(month: 11),
            FakeActionAgentModel.ReadBudget(month: 9),
            FakeActionAgentModel.ReadBudget(currency: "USD"),
            FakeActionAgentModel.Finish("Never reached."));

        var result = await Run(TestUsers.A, review.Id);

        AssertFailed(result, RunActionAgentHandler.MaxStepsReached);
        Assert.Equal(RunActionAgentHandler.MaxSteps, _model.Requests.Count);
        Assert.Equal(RunActionAgentHandler.MaxSteps, result.Trace!.StepCount);
        Assert.Equal(4, result.Trace.ToolCalls.Count);
    }

    [Theory]
    [InlineData("""{"year":2026,"month":10,"currency":"EUR"}""", """{"currency":"eur","month":10,"year":2026}""")]
    [InlineData("""{"year":2026,"month":10,"currency":"EUR"}""", """{"year":2026,"month":10,"currency":"EUR"}""")]
    public async Task ARepeatedIdenticalCall_EndsTheRun(string first, string second)
    {
        var review = AddReview(TestUsers.A);
        _model.Script(FakeActionAgentModel.Call(ActionAgentTools.GetBudgetStatus, first), FakeActionAgentModel.Call(ActionAgentTools.GetBudgetStatus, second));

        var result = await Run(TestUsers.A, review.Id);

        AssertFailed(result, RunActionAgentHandler.RepeatedToolCall);
        Assert.Equal(2, _model.Requests.Count);
        Assert.Equal([ActionAgentTools.GetBudgetStatus], result.Trace!.ToolCalls);
    }

    [Theory]
    [InlineData("set_monthly_budget")]
    [InlineData("apply_monthly_budget_adjustment")]
    [InlineData("run_sql")]
    [InlineData("")]
    public async Task AnUnknownTool_EndsTheRun_AndNothingIsExecuted(string tool)
    {
        var review = AddReview(TestUsers.A);
        await SetBudgetAsync(TestUsers.A, 400m);
        _model.Script(FakeActionAgentModel.Call(tool, """{"year":2026,"month":10,"currency":"EUR","amount":1}"""));

        var result = await Run(TestUsers.A, review.Id);

        AssertFailed(result, RunActionAgentHandler.UnknownTool);
        Assert.Equal(400m, (await _budgets.GetAsync(TestUsers.A, 2026, 10, "EUR", default))!.Amount);
    }

    [Theory]
    [InlineData(ActionAgentTools.GetBudgetStatus, """{"year":"2026","month":10,"currency":"EUR"}""", "integer year and month")]
    [InlineData(ActionAgentTools.GetBudgetStatus, """{"year":2026,"month":10}""", "integer year and month")]
    [InlineData(ActionAgentTools.GetBudgetStatus, """{"year":2026,"month":10,"currency":"EUR","user_id":"x"}""", "integer year and month")]
    [InlineData(ActionAgentTools.GetBudgetStatus, """{"year":2026,"month":10,"currency":"EURO"}""", "integer year and month")]
    [InlineData(ActionAgentTools.GetBudgetStatus, """{"year":2025,"month":10,"currency":"EUR"}""", "2026-09, 2026-10, 2026-11")]
    [InlineData(ActionAgentTools.GetWeeklyReview, """{"review_id":"x"}""", "no arguments")]
    [InlineData(ActionAgentTools.GetBudgetStatus, """[2026, 10, "EUR"]""", "integer year and month")]
    public async Task InvalidArguments_AreReturnedToTheModelAsAnErrorResult_AndTheRunContinues(string tool, string arguments, string message)
    {
        var review = AddReview(TestUsers.A);
        _model.Script(FakeActionAgentModel.Call(tool, arguments), FakeActionAgentModel.Finish("Could not read the budget."));

        var result = await Run(TestUsers.A, review.Id);

        Assert.Equal(ActionAgentRunStatus.NoAction, result.Status);
        var error = Assert.Single(_model.Requests[1].Steps).Result;
        Assert.Equal("invalid_arguments", error.GetProperty("error").GetString());
        Assert.Contains(message, error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AMalformedOrUnavailableModel_EndsTheRun_StoringNothing()
    {
        var review = AddReview(TestUsers.A);
        await SetBudgetAsync(TestUsers.A, 400m);

        _model.Script(FakeActionAgentModel.ReadReview(), FakeActionAgentModel.InvalidOutput);
        AssertFailed(await Run(TestUsers.A, review.Id), RunActionAgentHandler.InvalidOutput);

        _model.Script(FakeActionAgentModel.Unavailable);
        var unavailable = await Run(TestUsers.A, review.Id);
        Assert.Equal((ActionAgentRunStatus.Unavailable, RunActionAgentHandler.Unavailable), (unavailable.Status, unavailable.Trace!.ErrorCode));

        // A decision that does not match its kind is invalid output too.
        _model.Script(AgentStepResult.Success(new AgentDecision(AgentDecisionKind.ProposeBudgetAdjustment), FakeActionAgentModel.Identity));
        AssertFailed(await Run(TestUsers.A, review.Id), RunActionAgentHandler.InvalidOutput);
        _model.Script(FakeActionAgentModel.Finish("  "));
        AssertFailed(await Run(TestUsers.A, review.Id), RunActionAgentHandler.InvalidOutput);
        _model.Script(FakeActionAgentModel.Finish("Two\nlines."));
        AssertFailed(await Run(TestUsers.A, review.Id), RunActionAgentHandler.InvalidOutput);

        Assert.Empty(_proposals.Proposals);
        Assert.Equal(400m, (await _budgets.GetAsync(TestUsers.A, 2026, 10, "EUR", default))!.Amount);
    }

    [Fact]
    public async Task NoAction_IsAResultWithAReason_AndStoresNothing()
    {
        var review = AddReview(TestUsers.A);
        await SetBudgetAsync(TestUsers.A, 400m);
        _model.Script(FakeActionAgentModel.ReadReview(), FakeActionAgentModel.ReadBudget(), FakeActionAgentModel.Finish(" Your budget covers the month. "));

        var result = await Run(TestUsers.A, review.Id);

        Assert.Equal((ActionAgentRunStatus.NoAction, "Your budget covers the month."), (result.Status, result.Reason));
        Assert.False(result.Trace!.ProposalGenerated);
        Assert.Empty(_proposals.Proposals);
    }

    // ---- Server-side proposal validation ----

    [Fact]
    public async Task AProposalForAMonthOutsideTheTargets_IsRejected()
    {
        var review = AddReview(TestUsers.A);
        await SetBudgetAsync(TestUsers.A, 400m, month: 9);
        _model.Script(FakeActionAgentModel.ReadBudget(month: 9), FakeActionAgentModel.Propose(500m, month: 9));

        AssertFailed(await Run(TestUsers.A, review.Id), RunActionAgentHandler.ProposalMonthNotAllowed);
    }

    [Fact]
    public async Task AProposalForABudgetTheAgentDidNotRead_IsRejected()
    {
        var review = AddReview(TestUsers.A);
        await SetBudgetAsync(TestUsers.A, 400m);
        await SetBudgetAsync(TestUsers.A, 400m, currency: "USD");
        _model.Script(FakeActionAgentModel.ReadBudget(), FakeActionAgentModel.Propose(500m, currency: "USD"));

        AssertFailed(await Run(TestUsers.A, review.Id), RunActionAgentHandler.ProposalNotGrounded);
    }

    [Fact]
    public async Task AProposalWithoutAnExistingBudget_IsRejected()
    {
        var review = AddReview(TestUsers.A);
        _model.Script(FakeActionAgentModel.ReadBudget(month: 11), FakeActionAgentModel.Propose(500m, month: 11));

        AssertFailed(await Run(TestUsers.A, review.Id), RunActionAgentHandler.ProposalWithoutBudget);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(801)]
    [InlineData(199)]
    [InlineData(450.00001)]
    public async Task AProposalOutsideThePayloadContractOrPolicy_IsRejected(decimal amount)
    {
        var review = AddReview(TestUsers.A);
        await SetBudgetAsync(TestUsers.A, 400m);
        _model.Script(FakeActionAgentModel.ReadBudget(), FakeActionAgentModel.Propose(amount));

        AssertFailed(await Run(TestUsers.A, review.Id), RunActionAgentHandler.InvalidProposal);
    }

    [Fact]
    public async Task TheCurrentAmount_IsWhatLifeOSRead_NeverTheModel()
    {
        var review = AddReview(TestUsers.A);
        await SetBudgetAsync(TestUsers.A, 400m);
        _model.Script(FakeActionAgentModel.ReadBudget(currency: "eur"), FakeActionAgentModel.Propose(600m, currency: "eur"));

        var result = await Run(TestUsers.A, review.Id);

        Assert.Equal(new MonthlyBudgetAdjustment(2026, 10, "EUR", 400m, 600m), result.Proposal!.Payload);
    }

    // ---- Idempotency, ownership, concurrency ----

    [Fact]
    public async Task AnOpenProposal_IsReturnedWithoutAnAiCall()
    {
        var review = AddReview(TestUsers.A);
        await SetBudgetAsync(TestUsers.A, 400m);
        _model.Script(FakeActionAgentModel.ReadBudget(), FakeActionAgentModel.Propose(500m));
        var first = await Run(TestUsers.A, review.Id);
        var calls = _model.Requests.Count;

        var second = await Run(TestUsers.A, review.Id);

        Assert.Equal(ActionAgentRunStatus.Proposed, second.Status);
        Assert.Same(first.Proposal, second.Proposal);
        Assert.Equal(calls, _model.Requests.Count);
        Assert.Single(_proposals.Proposals);
    }

    [Fact]
    public async Task AnotherUsersReview_IsNotFound_WithoutAnAiCall()
    {
        var review = AddReview(TestUsers.B);

        var result = await Run(TestUsers.A, review.Id);

        Assert.Equal(ActionAgentRunStatus.NotFound, result.Status);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task AConcurrentRunsProposal_Wins()
    {
        var review = AddReview(TestUsers.A);
        await SetBudgetAsync(TestUsers.A, 400m);
        var winner = ProposedAction.ProposeBudgetAdjustment(TestUsers.A, review.Id, new MonthlyBudgetAdjustment(2026, 10, "EUR", 400m, 450m),
            "Stored first.", new AgentRunIdentity("fake", "m", "p", "t", [], 1), Now);
        _model.Script(FakeActionAgentModel.ReadBudget(), FakeActionAgentModel.Propose(500m));
        _model.BeforeAnswer = request => request.Steps.Count == 1 ? _proposals.TryAddAsync(winner, default) : Task.CompletedTask;

        var result = await Run(TestUsers.A, review.Id);

        Assert.Equal(ActionAgentRunStatus.Proposed, result.Status);
        Assert.Same(winner, result.Proposal);
        Assert.Single(_proposals.Proposals);
    }

    [Fact]
    public async Task TheTrace_DescribesTheRun_WithoutData()
    {
        var review = AddReview(TestUsers.A);
        await SetBudgetAsync(TestUsers.A, 400m);
        _model.Script(FakeActionAgentModel.ReadReview(), FakeActionAgentModel.ReadBudget(), FakeActionAgentModel.Propose(500m));

        var trace = (await Run(TestUsers.A, review.Id)).Trace!;

        Assert.Equal(FakeActionAgentModel.Identity, trace.Identity);
        Assert.Equal((ActionAgentTools.ToolSchemaVersion, 3, ActionAgentRunStatus.Proposed, (string?)null, true),
            (trace.ToolSchemaVersion, trace.StepCount, trace.Outcome, trace.ErrorCode, trace.ProposalGenerated));
        Assert.Equal([ActionAgentTools.GetWeeklyReview, ActionAgentTools.GetBudgetStatus], trace.ToolCalls);
        Assert.True(trace.ElapsedMs >= 0);
    }

    // ---- Helpers ----

    private Guid _budgetId;

    private Task<ActionAgentRunResult> Run(Guid userId, Guid reviewId) =>
        new RunActionAgentHandler(_reviews, _proposals, new ActionAgentTools(new GetMonthlyBudgetHandler(_budgets, _transactions, _clock)), _model, _clock)
            .HandleAsync(userId, reviewId, CancellationToken.None);

    private static void AssertFailed(ActionAgentRunResult result, string errorCode)
    {
        Assert.Equal(ActionAgentRunStatus.Failed, result.Status);
        Assert.Equal(errorCode, result.Trace!.ErrorCode);
        Assert.Null(result.Proposal);
    }

    private async Task SetBudgetAsync(Guid userId, decimal amount, int month = 10, string currency = "EUR")
    {
        var budget = MonthlyBudget.Create(userId, 2026, month, currency, amount);
        _budgetId = budget.Id;
        await _budgets.SetAsync(budget, default);
    }

    private async Task SpendAsync(Guid userId, decimal amount)
    {
        await _transactions.TryAddAsync(Transaction.CreateExpense(userId, Guid.CreateVersion7(), Guid.CreateVersion7(), amount, "EUR",
            new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero), null, Now), default);
    }

    private WeeklyReview AddReview(Guid userId)
    {
        var review = WeeklyReview.Create(userId, WeekEnd, "Europe/Rome", Now.AddDays(-3), new WeeklyReviewSnapshot(
            new WeeklyFinanceSummary([new WeeklyCurrencySummary("EUR", 380m, 0m, -380m, [new WeeklyExpenseCategory("Groceries", 380m)])]),
            new WeeklyGymSummary(0, 0, 0, 0, []),
            new WeeklyNutritionSummary(0, 0, 0, 0, 0m, 0m, 0m, 0m, [])));
        _reviews.Reviews.Add(review);

        return review;
    }
}
