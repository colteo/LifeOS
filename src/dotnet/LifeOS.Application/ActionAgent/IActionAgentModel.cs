using System.Text.Json;
using LifeOS.Domain.ActionAgent;

namespace LifeOS.Application.ActionAgent;

// AI-002: ONE step of the Action Agent's reasoning. Implemented in Infrastructure by a client of the
// Python AI service; no provider, framework or transport is visible here.
//
// The model only decides. It receives the read-only tool definitions LifeOS offers and the results of
// the tools LifeOS already executed, and answers with exactly one decision: call one of those tools,
// propose one budget adjustment, or finish without a proposal. It has no tool that writes, never sees
// user ids, review ids or tokens, and nothing it returns is executed: LifeOS validates every decision,
// executes read tools itself and only ever stores a Pending proposal.
public interface IActionAgentModel
{
    // Never throws for expected failures (unreachable, timeout, unusable output): those are results.
    Task<AgentStepResult> NextStepAsync(AgentStepRequest request, CancellationToken cancellationToken);
}

// A read-only tool LifeOS offers: name, description and the JSON Schema of its arguments.
public sealed record AgentToolDefinition(string Name, string Description, JsonElement Parameters);

// A tool call LifeOS already executed, with the result it returned to the model (or an error result
// for invalid arguments).
public sealed record AgentToolStep(string Tool, JsonElement Arguments, JsonElement Result);

// The task framing: the local month today and the months a budget adjustment may target ("yyyy-MM").
public sealed record AgentTaskContext(string CurrentMonth, IReadOnlyList<string> TargetMonths);

// Everything one step may know. MaxSteps lets the service force a final answer on the last step.
public sealed record AgentStepRequest(
    string ToolSchemaVersion,
    AgentTaskContext Context,
    IReadOnlyList<AgentToolDefinition> Tools,
    IReadOnlyList<AgentToolStep> Steps,
    int MaxSteps);

public enum AgentDecisionKind
{
    CallTool,
    ProposeBudgetAdjustment,
    NoAction
}

// What the model proposes: the target budget and the new amount. Not trusted: LifeOS checks it against
// what its own tools observed before anything is stored.
public sealed record AgentBudgetProposal(int Year, int Month, string Currency, decimal ProposedAmount, string Rationale);

public sealed record AgentDecision(
    AgentDecisionKind Kind,
    string? Tool = null,
    JsonElement? Arguments = null,
    AgentBudgetProposal? Proposal = null,
    string? Reason = null)
{
    public static AgentDecision CallTool(string tool, JsonElement arguments) => new(AgentDecisionKind.CallTool, tool, arguments);

    public static AgentDecision Propose(AgentBudgetProposal proposal) => new(AgentDecisionKind.ProposeBudgetAdjustment, Proposal: proposal);

    public static AgentDecision Finish(string reason) => new(AgentDecisionKind.NoAction, Reason: reason);
}

// Provider, model and prompt version that produced a step.
public sealed record AgentModelIdentity(string Provider, string Model, string PromptVersion);

public enum AgentStepFailure
{
    // Not configured, unreachable, timed out or rate-limited: try again later.
    Unavailable,

    // The model answered without a valid decision (rejected, malformed, unknown shape).
    InvalidOutput
}

public sealed record AgentStepResult(AgentDecision? Decision, AgentModelIdentity? Identity, AgentStepFailure? Failure)
{
    public static AgentStepResult Success(AgentDecision decision, AgentModelIdentity identity) => new(decision, identity, null);

    public static AgentStepResult Failed(AgentStepFailure failure) => new(null, null, failure);
}

// AI-002 persistence of proposals. Every read is scoped to the owner: another user's proposal is
// indistinguishable from a missing one. There is no way to change a payload: proposals are added once
// and afterwards only their status moves, conditionally on the status the caller read.
public interface IProposedActionRepository
{
    Task<ProposedAction?> GetAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken);

    // The review's most recent proposal, whatever its status.
    Task<ProposedAction?> GetLatestForReviewAsync(Guid userId, Guid reviewId, CancellationToken cancellationToken);

    // Inserts a Pending proposal unless the review already has an open (Pending/Approved) one or no
    // longer exists (then false, nothing written).
    Task<bool> TryAddAsync(ProposedAction proposal, CancellationToken cancellationToken);

    // Writes the status, decision/execution timestamps and failure code of `updated`, only if the stored
    // row still has status `expected` (then true). Never touches the payload. Joins the caller's unit of
    // work when there is one.
    Task<bool> TryUpdateStatusAsync(ProposedAction updated, ProposedActionStatus expected, CancellationToken cancellationToken);
}
