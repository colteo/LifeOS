using System.Diagnostics;
using System.Text.Json;
using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.ActionAgent;

namespace LifeOS.Application.ActionAgent;

public enum ActionAgentRunStatus
{
    // No such review for this user.
    NotFound,

    // A Pending proposal was created, or the review already had an open one (returned without an AI call).
    Proposed,

    // The agent finished without proposing anything.
    NoAction,

    // The AI service cannot answer now. Nothing was stored.
    Unavailable,

    // The agent run ended without a valid result (see ErrorCode). Nothing was stored.
    Failed
}

// Technical facts about one run, safe to log: identities, fixed tool names, counts, latency, codes.
// Never the review, budgets, arguments, results, prompts or model text.
public sealed record ActionAgentRunTrace(
    AgentModelIdentity? Identity,
    string ToolSchemaVersion,
    IReadOnlyList<string> ToolCalls,
    int StepCount,
    long ElapsedMs,
    ActionAgentRunStatus Outcome,
    string? ErrorCode,
    bool ProposalGenerated);

public sealed record ActionAgentRunResult(
    ActionAgentRunStatus Status,
    ProposedAction? Proposal = null,
    string? Reason = null,
    ActionAgentRunTrace? Trace = null);

// AI-002: the Action Agent run, on demand for one saved weekly review. A bounded tool-use loop:
//
//   model step → (read-only tool executed by LifeOS → result back to the model)* → proposal | no action
//
// The model chooses which read-only tool to call next and whether anything is worth proposing; LifeOS
// executes the tools, enforces every bound and validates the final answer. Stop conditions:
// - a final decision (proposal or no action);
// - MaxSteps model steps (the last step must be final; a tool call there ends the run);
// - an unknown tool, a repeated identical tool call, an unavailable or invalid model answer.
// Invalid tool ARGUMENTS are returned to the model as an error result (it may correct them within the
// step budget). The run is synchronous and leaves nothing running when it returns.
//
// It never writes LifeOS data: its only write is a Pending proposal, which does nothing until the user
// approves it (ApproveProposedActionHandler).
public sealed class RunActionAgentHandler(
    IWeeklyReviewRepository reviews,
    IProposedActionRepository proposals,
    ActionAgentTools tools,
    IActionAgentModel model,
    TimeProvider clock)
{
    public const int MaxSteps = 5;
    public const int MaxReasonLength = 300;

    // Run error codes (stable; logged and returned to the API, never shown as model or exception text).
    public const string InvalidOutput = "invalid_output";
    public const string UnknownTool = "unknown_tool";
    public const string RepeatedToolCall = "repeated_tool_call";
    public const string MaxStepsReached = "max_steps";
    public const string ProposalMonthNotAllowed = "proposal_month_not_allowed";
    public const string ProposalNotGrounded = "proposal_not_grounded";
    public const string ProposalWithoutBudget = "proposal_without_budget";
    public const string InvalidProposal = "invalid_proposal";
    public const string Unavailable = "unavailable";

    private static readonly JsonElement EmptyArguments = JsonDocument.Parse("{}").RootElement.Clone();

    public async Task<ActionAgentRunResult> HandleAsync(Guid userId, Guid reviewId, CancellationToken cancellationToken)
    {
        var review = await reviews.GetAsync(userId, reviewId, cancellationToken);

        if (review is null)
        {
            return new(ActionAgentRunStatus.NotFound);
        }

        // One open proposal per review: an undecided suggestion is returned as it is, without an AI call.
        if (await proposals.GetLatestForReviewAsync(userId, reviewId, cancellationToken) is { IsOpen: true } open)
        {
            return new(ActionAgentRunStatus.Proposed, open);
        }

        var started = Stopwatch.GetTimestamp();
        var run = new Run(Scope(userId, review), started);

        for (var step = 1; step <= MaxSteps; step++)
        {
            var answer = await model.NextStepAsync(run.Request(), cancellationToken);
            run.Steps = step;

            if (answer is not { Decision: { } decision, Identity: { } identity })
            {
                return answer.Failure == AgentStepFailure.Unavailable
                    ? run.End(ActionAgentRunStatus.Unavailable, Unavailable)
                    : run.End(ActionAgentRunStatus.Failed, InvalidOutput);
            }

            run.Identity = identity;

            switch (decision.Kind)
            {
                case AgentDecisionKind.CallTool:
                    if (step == MaxSteps)
                    {
                        return run.End(ActionAgentRunStatus.Failed, MaxStepsReached);
                    }

                    if (decision.Tool is not { } tool || !ActionAgentTools.IsTool(tool))
                    {
                        return run.End(ActionAgentRunStatus.Failed, UnknownTool);
                    }

                    var arguments = decision.Arguments is { ValueKind: not JsonValueKind.Undefined } given ? given : EmptyArguments;

                    if (!run.CallKeys.Add(ActionAgentTools.CallKey(tool, arguments)))
                    {
                        return run.End(ActionAgentRunStatus.Failed, RepeatedToolCall);
                    }

                    var outcome = await tools.ExecuteAsync(run.Scope, tool, arguments, cancellationToken);
                    run.Executed.Add(new AgentToolStep(tool, arguments, outcome.Result));

                    if (outcome.Observation is { } observation)
                    {
                        run.Observations[(observation.Month, observation.Currency)] = observation;
                    }

                    break;

                case AgentDecisionKind.ProposeBudgetAdjustment when decision.Proposal is { } proposed:
                    return await ProposeAsync(run, review.Id, proposed, cancellationToken);

                case AgentDecisionKind.NoAction when Reason(decision.Reason) is { } reason:
                    return run.End(ActionAgentRunStatus.NoAction, null) with { Reason = reason };

                default:
                    return run.End(ActionAgentRunStatus.Failed, InvalidOutput);
            }
        }

        return run.End(ActionAgentRunStatus.Failed, MaxStepsReached);
    }

    // Server-side validation of the model's proposal. It must target an allowed month, and a budget that
    // LifeOS itself read during this run and that exists; the current amount is LifeOS's observation,
    // never the model's. The Domain then applies the payload and agent-policy invariants.
    private async Task<ActionAgentRunResult> ProposeAsync(Run run, Guid reviewId, AgentBudgetProposal proposed, CancellationToken cancellationToken)
    {
        var month = new AgentMonth(proposed.Year, proposed.Month);

        if (!run.Scope.TargetMonths.Contains(month))
        {
            return run.End(ActionAgentRunStatus.Failed, ProposalMonthNotAllowed);
        }

        var currency = (proposed.Currency ?? "").Trim().ToUpperInvariant();

        if (!run.Observations.TryGetValue((month, currency), out var observation))
        {
            return run.End(ActionAgentRunStatus.Failed, ProposalNotGrounded);
        }

        if (observation.Amount is not { } current)
        {
            return run.End(ActionAgentRunStatus.Failed, ProposalWithoutBudget);
        }

        ProposedAction created;

        try
        {
            created = ProposedAction.ProposeBudgetAdjustment(
                run.Scope.UserId,
                reviewId,
                new MonthlyBudgetAdjustment(month.Year, month.Month, currency, current, proposed.ProposedAmount),
                proposed.Rationale,
                new AgentRunIdentity(run.Identity!.Provider, run.Identity.Model, run.Identity.PromptVersion,
                    ActionAgentTools.ToolSchemaVersion, run.ToolNames, run.Steps),
                clock.GetUtcNow());
        }
        catch (ArgumentException)
        {
            return run.End(ActionAgentRunStatus.Failed, InvalidProposal);
        }

        if (await proposals.TryAddAsync(created, cancellationToken))
        {
            return run.End(ActionAgentRunStatus.Proposed, null, created) with { Proposal = created };
        }

        // A concurrent run stored an open proposal first (it wins), or the review was deleted meanwhile.
        return await proposals.GetLatestForReviewAsync(run.Scope.UserId, reviewId, cancellationToken) is { IsOpen: true } winner
            ? run.End(ActionAgentRunStatus.Proposed, null) with { Proposal = winner }
            : run.End(ActionAgentRunStatus.NotFound, null);
    }

    private AgentRunScope Scope(Guid userId, Domain.WeeklyReviews.WeeklyReview review)
    {
        // The review's zone is valid by construction (AUTO-002 resolves it before creating the review).
        var zone = TimeZoneInfo.FindSystemTimeZoneById(review.TimeZoneId);
        var now = clock.GetUtcNow();
        var current = AgentMonth.Of(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime));
        var targets = new List<AgentMonth> { current, current.Next() };

        // Readable: the review's month(s) for context, plus the targets.
        var readable = new List<AgentMonth> { AgentMonth.Of(review.WeekStartDate), AgentMonth.Of(review.WeekEndDate) }
            .Concat(targets)
            .Distinct()
            .OrderBy(month => month.Year)
            .ThenBy(month => month.Month)
            .ToList();

        return new AgentRunScope(userId, review, zone, now, readable, targets);
    }

    private static string? Reason(string? reason)
    {
        var text = reason?.Trim();

        return string.IsNullOrEmpty(text) || text.Length > MaxReasonLength || text.Any(char.IsControl) ? null : text;
    }

    // The mutable state of one run, private to it.
    private sealed class Run(AgentRunScope scope, long started)
    {
        public AgentRunScope Scope { get; } = scope;

        public List<AgentToolStep> Executed { get; } = [];

        public HashSet<string> CallKeys { get; } = new(StringComparer.Ordinal);

        public Dictionary<(AgentMonth, string), BudgetObservation> Observations { get; } = [];

        public AgentModelIdentity? Identity { get; set; }

        public int Steps { get; set; }

        public IReadOnlyList<string> ToolNames => Executed.Select(step => step.Tool).ToList();

        public AgentStepRequest Request() => new(
            ActionAgentTools.ToolSchemaVersion,
            new AgentTaskContext(Scope.TargetMonths[0].ToString(), Scope.TargetMonths.Select(month => month.ToString()).ToList()),
            ActionAgentTools.Definitions,
            Executed.ToList(),
            MaxSteps);

        public ActionAgentRunResult End(ActionAgentRunStatus status, string? errorCode, ProposedAction? proposal = null) =>
            new(status, Trace: new ActionAgentRunTrace(Identity, ActionAgentTools.ToolSchemaVersion, ToolNames, Steps,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, status, errorCode, proposal is not null));
    }
}
