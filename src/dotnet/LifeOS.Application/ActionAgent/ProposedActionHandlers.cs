using LifeOS.Application.Finance.Budgets;
using LifeOS.Application.Persistence;
using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.ActionAgent;

namespace LifeOS.Application.ActionAgent;

public enum ProposedActionResultStatus
{
    // No such proposal (or review) for this user.
    NotFound,

    Ok,

    // The requested decision contradicts the proposal's state (e.g. approving a rejected proposal).
    Conflict
}

public sealed record ProposedActionResult(ProposedActionResultStatus Status, ProposedAction? Proposal = null)
{
    public static ProposedActionResult NotFound { get; } = new(ProposedActionResultStatus.NotFound);

    public static ProposedActionResult Ok(ProposedAction? proposal) => new(ProposedActionResultStatus.Ok, proposal);

    public static ProposedActionResult Conflict(ProposedAction proposal) => new(ProposedActionResultStatus.Conflict, proposal);
}

public sealed class GetProposedActionHandler(IProposedActionRepository proposals)
{
    public async Task<ProposedActionResult> HandleAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken) =>
        await proposals.GetAsync(userId, proposalId, cancellationToken) is { } proposal
            ? ProposedActionResult.Ok(proposal)
            : ProposedActionResult.NotFound;
}

// The review's latest proposal (Ok with null when there is none). Never calls the AI service.
public sealed class GetReviewProposedActionHandler(IWeeklyReviewRepository reviews, IProposedActionRepository proposals)
{
    public async Task<ProposedActionResult> HandleAsync(Guid userId, Guid reviewId, CancellationToken cancellationToken)
    {
        if (await reviews.GetAsync(userId, reviewId, cancellationToken) is null)
        {
            return ProposedActionResult.NotFound;
        }

        return ProposedActionResult.Ok(await proposals.GetLatestForReviewAsync(userId, reviewId, cancellationToken));
    }
}

// AI-002: the human approval boundary. The user's explicit approval is the ONLY way a proposal reaches
// LifeOS data, and it does so through the existing deterministic Finance command (SetMonthlyBudget),
// never through anything the model produced at execution time: the stored, validated payload is the
// command's input.
//
// Pending → Approved (the decision, recorded first) → Executed | Failed (the execution, atomic with the
// budget write). Idempotent: approving an Executed or Failed proposal returns it unchanged; an Approved
// one left behind (e.g. a crash after the decision) is executed by the next approval. A Rejected
// proposal can never be executed (Conflict). The budget must still have the amount the proposal was
// made against; otherwise nothing is written and the proposal fails with BudgetChanged.
public sealed class ApproveProposedActionHandler(
    IProposedActionRepository proposals,
    IMonthlyBudgetRepository budgets,
    SetMonthlyBudgetHandler setBudget,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    private const int MaxAttempts = 3;

    public async Task<ProposedActionResult> HandleAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var proposal = await proposals.GetAsync(userId, proposalId, cancellationToken);

            switch (proposal?.Status)
            {
                case null:
                    return ProposedActionResult.NotFound;

                case ProposedActionStatus.Rejected:
                    return ProposedActionResult.Conflict(proposal);

                case ProposedActionStatus.Executed or ProposedActionStatus.Failed:
                    return ProposedActionResult.Ok(proposal);

                case ProposedActionStatus.Pending:
                    var approved = proposal.Approve(clock.GetUtcNow());

                    if (await proposals.TryUpdateStatusAsync(approved, ProposedActionStatus.Pending, cancellationToken))
                    {
                        return await ExecuteAsync(approved, cancellationToken);
                    }

                    // Decided concurrently: read it again.
                    continue;

                case ProposedActionStatus.Approved:
                    return await ExecuteAsync(proposal, cancellationToken);
            }
        }

        return await proposals.GetAsync(userId, proposalId, cancellationToken) is { } current
            ? ProposedActionResult.Ok(current)
            : ProposedActionResult.NotFound;
    }

    private async Task<ProposedActionResult> ExecuteAsync(ProposedAction approved, CancellationToken cancellationToken)
    {
        var payload = approved.Payload;
        string? failure = null;
        var executed = approved.MarkExecuted(clock.GetUtcNow());

        var committed = await unitOfWork.TryInTransactionAsync(async ct =>
        {
            var budget = await budgets.GetAsync(approved.UserId, payload.Year, payload.Month, payload.Currency, ct);

            if (budget?.Amount != payload.CurrentAmount)
            {
                failure = ProposedAction.BudgetChanged;
                return false;
            }

            // The existing deterministic command validates before it writes.
            var set = await setBudget.HandleAsync(approved.UserId,
                new SetMonthlyBudgetCommand(payload.Year, payload.Month, payload.Currency, payload.ProposedAmount), ct);

            if (set.Status != MonthlyBudgetStatus.Ok)
            {
                failure = ProposedAction.BudgetRejected;
                return false;
            }

            // Claims the execution; a concurrent approval that already executed (or failed) it wins, and
            // this transaction, including its budget write, rolls back.
            return await proposals.TryUpdateStatusAsync(executed, ProposedActionStatus.Approved, ct);
        }, cancellationToken);

        if (committed)
        {
            return ProposedActionResult.Ok(executed);
        }

        if (failure is not null)
        {
            await proposals.TryUpdateStatusAsync(approved.MarkFailed(failure), ProposedActionStatus.Approved, cancellationToken);
        }

        // Whatever is stored now (this request's failure, or a concurrent request's outcome).
        return await proposals.GetAsync(approved.UserId, approved.Id, cancellationToken) is { } current
            ? ProposedActionResult.Ok(current)
            : ProposedActionResult.NotFound;
    }
}

// "Not now": Pending → Rejected. Idempotent for a Rejected proposal; an approved, executed or failed
// one cannot be rejected (Conflict). Nothing in Finance is touched.
public sealed class RejectProposedActionHandler(IProposedActionRepository proposals, TimeProvider clock)
{
    private const int MaxAttempts = 3;

    public async Task<ProposedActionResult> HandleAsync(Guid userId, Guid proposalId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var proposal = await proposals.GetAsync(userId, proposalId, cancellationToken);

            switch (proposal?.Status)
            {
                case null:
                    return ProposedActionResult.NotFound;

                case ProposedActionStatus.Rejected:
                    return ProposedActionResult.Ok(proposal);

                case ProposedActionStatus.Pending:
                    var rejected = proposal.Reject(clock.GetUtcNow());

                    if (await proposals.TryUpdateStatusAsync(rejected, ProposedActionStatus.Pending, cancellationToken))
                    {
                        return ProposedActionResult.Ok(rejected);
                    }

                    continue;

                default:
                    return ProposedActionResult.Conflict(proposal);
            }
        }

        return await proposals.GetAsync(userId, proposalId, cancellationToken) is { } current
            ? ProposedActionResult.Ok(current)
            : ProposedActionResult.NotFound;
    }
}
