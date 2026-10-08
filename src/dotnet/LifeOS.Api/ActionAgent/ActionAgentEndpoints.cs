using LifeOS.Api.Authentication;
using LifeOS.Application.ActionAgent;
using LifeOS.Contracts.ActionAgent;
using LifeOS.Domain.ActionAgent;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.ActionAgent;

// AI-002: the Action Agent's suggested action for a weekly review, and the user's decision on it.
// Transport only. The owner comes only from the access token; another user's review or proposal is a 404,
// indistinguishable from a missing one. Problem details carry fixed messages only.
public static class ActionAgentEndpoints
{
    public const string UnavailableMessage = "The assistant is unavailable right now. Try again later.";
    public const string FailedMessage = "The assistant could not suggest an action for this review. Try again later.";
    public const string RejectedConflictMessage = "This suggestion was dismissed and can no longer be applied.";
    public const string DecidedConflictMessage = "This suggestion was already approved and can no longer be dismissed.";

    public static IEndpointRouteBuilder MapActionAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var reviews = endpoints.MapGroup("/api/weekly-reviews").RequireAuthorization();
        reviews.MapGet("/{reviewId:guid}/suggested-action", GetForReviewAsync).WithName("GetSuggestedAction");
        reviews.MapPost("/{reviewId:guid}/suggested-action", AnalyzeAsync).WithName("AnalyzeSuggestedAction");

        var proposals = endpoints.MapGroup("/api/action-proposals").RequireAuthorization();
        proposals.MapGet("/{proposalId:guid}", GetAsync).WithName("GetActionProposal");
        proposals.MapPost("/{proposalId:guid}/approve", ApproveAsync).WithName("ApproveActionProposal");
        proposals.MapPost("/{proposalId:guid}/reject", RejectAsync).WithName("RejectActionProposal");

        return endpoints;
    }

    // Never calls the AI service.
    public static async Task<Results<Ok<SuggestedActionStateResponse>, ProblemHttpResult>> GetForReviewAsync(
        Guid reviewId,
        AuthenticatedUser user,
        GetReviewProposedActionHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId, reviewId, cancellationToken);

        if (result.Status == ProposedActionResultStatus.NotFound)
        {
            return ReviewNotFound();
        }

        return TypedResults.Ok(result.Proposal is { } proposal
            ? new SuggestedActionStateResponse(SuggestedActionStatuses.Proposal, ToResponse(proposal), null)
            : new SuggestedActionStateResponse(SuggestedActionStatuses.None, null, null));
    }

    // Runs the agent (or returns the review's open proposal without an AI call). Never writes Finance:
    // at most a Pending proposal is stored.
    public static async Task<Results<Ok<SuggestedActionStateResponse>, ProblemHttpResult>> AnalyzeAsync(
        Guid reviewId,
        AuthenticatedUser user,
        RunActionAgentHandler handler,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId, reviewId, cancellationToken);

        if (result.Trace is { } trace)
        {
            Log(loggerFactory.CreateLogger("LifeOS.Api.ActionAgent"), trace);
        }

        return result.Status switch
        {
            ActionAgentRunStatus.Proposed => TypedResults.Ok(
                new SuggestedActionStateResponse(SuggestedActionStatuses.Proposal, ToResponse(result.Proposal!), null)),
            ActionAgentRunStatus.NoAction => TypedResults.Ok(
                new SuggestedActionStateResponse(SuggestedActionStatuses.NoAction, null, result.Reason)),
            ActionAgentRunStatus.NotFound => ReviewNotFound(),
            ActionAgentRunStatus.Unavailable => TypedResults.Problem(
                title: "Assistant unavailable", detail: UnavailableMessage, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => TypedResults.Problem(
                title: "No suggestion", detail: FailedMessage, statusCode: StatusCodes.Status502BadGateway)
        };
    }

    public static async Task<Results<Ok<ProposedActionResponse>, ProblemHttpResult>> GetAsync(
        Guid proposalId,
        AuthenticatedUser user,
        GetProposedActionHandler handler,
        CancellationToken cancellationToken) =>
        ToDecisionResult(await handler.HandleAsync(user.UserId, proposalId, cancellationToken), "");

    // The explicit approval: executes the stored proposal through the Finance budget command.
    // Idempotent: a repeated approval returns the executed (or failed) proposal without writing again.
    public static async Task<Results<Ok<ProposedActionResponse>, ProblemHttpResult>> ApproveAsync(
        Guid proposalId,
        AuthenticatedUser user,
        ApproveProposedActionHandler handler,
        CancellationToken cancellationToken) =>
        ToDecisionResult(await handler.HandleAsync(user.UserId, proposalId, cancellationToken), RejectedConflictMessage);

    public static async Task<Results<Ok<ProposedActionResponse>, ProblemHttpResult>> RejectAsync(
        Guid proposalId,
        AuthenticatedUser user,
        RejectProposedActionHandler handler,
        CancellationToken cancellationToken) =>
        ToDecisionResult(await handler.HandleAsync(user.UserId, proposalId, cancellationToken), DecidedConflictMessage);

    private static Results<Ok<ProposedActionResponse>, ProblemHttpResult> ToDecisionResult(ProposedActionResult result, string conflictMessage) =>
        result.Status switch
        {
            ProposedActionResultStatus.Ok => TypedResults.Ok(ToResponse(result.Proposal!)),
            ProposedActionResultStatus.Conflict => TypedResults.Problem(
                title: "Suggestion already decided", detail: conflictMessage, statusCode: StatusCodes.Status409Conflict),
            _ => TypedResults.Problem(
                title: "Suggestion not found.", detail: "This suggestion does not exist.", statusCode: StatusCodes.Status404NotFound)
        };

    private static ProblemHttpResult ReviewNotFound() => TypedResults.Problem(
        title: "Weekly review not found.", detail: "This weekly review does not exist.", statusCode: StatusCodes.Status404NotFound);

    internal static ProposedActionResponse ToResponse(ProposedAction proposal) => new(
        proposal.Id,
        proposal.ReviewId,
        proposal.ActionType.ToString(),
        proposal.Status.ToString(),
        new MonthlyBudgetAdjustmentResponse(proposal.Payload.Year, proposal.Payload.Month, proposal.Payload.Currency,
            proposal.Payload.CurrentAmount, proposal.Payload.ProposedAmount),
        proposal.Rationale,
        proposal.CreatedAtUtc,
        proposal.DecidedAtUtc,
        proposal.ExecutedAtUtc,
        proposal.FailureCode,
        proposal.Run.Provider,
        proposal.Run.Model,
        proposal.Run.PromptVersion);

    // One line per run with technical metadata only: never the review, budgets, arguments, tool results,
    // prompts, rationale or model text.
    private static void Log(ILogger logger, ActionAgentRunTrace trace) =>
        logger.LogInformation(
            "Action agent run {Outcome} (error {ErrorCode}) by {Provider} {Model} with prompt {PromptVersion} and tools {ToolSchemaVersion}: "
            + "calls [{ToolCalls}], {StepCount} steps, {ElapsedMs} ms, proposal generated {ProposalGenerated}.",
            trace.Outcome,
            trace.ErrorCode ?? "none",
            trace.Identity?.Provider ?? "-",
            trace.Identity?.Model ?? "-",
            trace.Identity?.PromptVersion ?? "-",
            trace.ToolSchemaVersion,
            string.Join(",", trace.ToolCalls),
            trace.StepCount,
            trace.ElapsedMs,
            trace.ProposalGenerated);
}
