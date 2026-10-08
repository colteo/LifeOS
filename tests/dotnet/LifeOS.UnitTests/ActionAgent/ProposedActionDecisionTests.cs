using LifeOS.Application.ActionAgent;
using LifeOS.Application.Finance.Budgets;
using LifeOS.Domain.ActionAgent;
using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.WeeklyReviews;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.ActionAgent;

// AI-002: the human approval boundary. Execution only through the user's approval and the existing
// SetMonthlyBudget command; idempotent approval; rejected proposals never execute; a changed budget is
// never overwritten; another user's proposal does not exist.
public class ProposedActionDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);

    private readonly InMemoryProposedActionRepository _proposals = new();
    private readonly CountingBudgetRepository _budgets = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly ManualTimeProvider _clock = new(Now);

    [Fact]
    public async Task Approve_ExecutesThroughTheBudgetCommand_AndRecordsTheTransitions()
    {
        await SetBudgetAsync(TestUsers.A, 400m);
        var proposal = await AddPendingAsync(TestUsers.A);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await Approve(TestUsers.A, proposal.Id);

        Assert.Equal(ProposedActionResultStatus.Ok, result.Status);
        var executed = result.Proposal!;
        Assert.Equal(ProposedActionStatus.Executed, executed.Status);
        Assert.Equal((Now.AddMinutes(5), Now.AddMinutes(5)), (executed.DecidedAtUtc!.Value, executed.ExecutedAtUtc!.Value));
        Assert.Equal(500m, (await _budgets.GetAsync(TestUsers.A, 2026, 10, "EUR", default))!.Amount);
        Assert.Equal(2, _budgets.Sets); // the test's own budget, then the approved adjustment
        Assert.Equal(1, _budgets.LockedReads);
        Assert.Equal(1, _unitOfWork.Committed);
        Assert.Equal(ProposedActionStatus.Executed, Stored(proposal.Id).Status);
    }

    [Fact]
    public async Task ApprovingTwice_ExecutesOnce_AndReturnsTheExecutedProposal()
    {
        await SetBudgetAsync(TestUsers.A, 400m);
        var proposal = await AddPendingAsync(TestUsers.A);
        var first = await Approve(TestUsers.A, proposal.Id);

        // The user then edits the budget by hand: a replayed approval must not overwrite it.
        await SetBudgetAsync(TestUsers.A, 470m);
        var second = await Approve(TestUsers.A, proposal.Id);

        Assert.Equal(ProposedActionStatus.Executed, second.Proposal!.Status);
        Assert.Equal(first.Proposal!.ExecutedAtUtc, second.Proposal.ExecutedAtUtc);
        Assert.Equal(470m, (await _budgets.GetAsync(TestUsers.A, 2026, 10, "EUR", default))!.Amount);
        Assert.Equal(3, _budgets.Sets); // setup, the one execution, the user's edit
    }

    [Fact]
    public async Task ARejectedProposal_CannotBeExecuted()
    {
        await SetBudgetAsync(TestUsers.A, 400m);
        var proposal = await AddPendingAsync(TestUsers.A);

        var rejected = await Reject(TestUsers.A, proposal.Id);
        var approved = await Approve(TestUsers.A, proposal.Id);

        Assert.Equal(ProposedActionStatus.Rejected, rejected.Proposal!.Status);
        Assert.Equal(ProposedActionResultStatus.Conflict, approved.Status);
        Assert.Equal(ProposedActionStatus.Rejected, Stored(proposal.Id).Status);
        Assert.Equal(400m, (await _budgets.GetAsync(TestUsers.A, 2026, 10, "EUR", default))!.Amount);
        Assert.Equal(1, _budgets.Sets);
    }

    [Fact]
    public async Task Reject_IsIdempotent_ButCannotUndoAnApproval()
    {
        await SetBudgetAsync(TestUsers.A, 400m);
        var pending = await AddPendingAsync(TestUsers.A);
        Assert.Equal(ProposedActionStatus.Rejected, (await Reject(TestUsers.A, pending.Id)).Proposal!.Status);
        Assert.Equal(ProposedActionResultStatus.Ok, (await Reject(TestUsers.A, pending.Id)).Status);

        var other = await AddPendingAsync(TestUsers.A, reviewId: Guid.CreateVersion7());
        await Approve(TestUsers.A, other.Id);

        var late = await Reject(TestUsers.A, other.Id);

        Assert.Equal(ProposedActionResultStatus.Conflict, late.Status);
        Assert.Equal(ProposedActionStatus.Executed, Stored(other.Id).Status);
    }

    [Theory]
    [InlineData(420)]
    [InlineData(null)]
    public async Task AChangedOrDeletedBudget_IsNeverOverwritten_TheProposalFails(int? changedTo)
    {
        await SetBudgetAsync(TestUsers.A, 400m);
        var proposal = await AddPendingAsync(TestUsers.A);

        if (changedTo is { } amount)
        {
            await SetBudgetAsync(TestUsers.A, amount);
        }
        else
        {
            await _budgets.DeleteAsync(TestUsers.A, 2026, 10, "EUR", default);
        }

        var setsBefore = _budgets.Sets;
        var result = await Approve(TestUsers.A, proposal.Id);

        Assert.Equal((ProposedActionStatus.Failed, ProposedAction.BudgetChanged), (result.Proposal!.Status, result.Proposal.FailureCode));
        Assert.Equal(setsBefore, _budgets.Sets);
        Assert.Equal(1, _unitOfWork.RolledBack);
        Assert.Equal(changedTo, (int?)(await _budgets.GetAsync(TestUsers.A, 2026, 10, "EUR", default))?.Amount);

        // A failed proposal stays failed: approving again writes nothing.
        Assert.Equal(ProposedActionStatus.Failed, (await Approve(TestUsers.A, proposal.Id)).Proposal!.Status);
        Assert.Equal(setsBefore, _budgets.Sets);
    }

    [Fact]
    public async Task AnApprovalLeftBehind_IsExecutedByTheNextApproval()
    {
        await SetBudgetAsync(TestUsers.A, 400m);
        var pending = await AddPendingAsync(TestUsers.A);
        Assert.True(await _proposals.TryUpdateStatusAsync(pending.Approve(Now), ProposedActionStatus.Pending, default));

        var result = await Approve(TestUsers.A, pending.Id);

        Assert.Equal(ProposedActionStatus.Executed, result.Proposal!.Status);
        Assert.Equal(Now, result.Proposal.DecidedAtUtc);
        Assert.Equal(500m, (await _budgets.GetAsync(TestUsers.A, 2026, 10, "EUR", default))!.Amount);
    }

    [Fact]
    public async Task AnotherUsersProposal_DoesNotExist_ForAnyOperation()
    {
        await SetBudgetAsync(TestUsers.A, 400m);
        var proposal = await AddPendingAsync(TestUsers.A);

        Assert.Equal(ProposedActionResultStatus.NotFound, (await new GetProposedActionHandler(_proposals).HandleAsync(TestUsers.B, proposal.Id, default)).Status);
        Assert.Equal(ProposedActionResultStatus.NotFound, (await Approve(TestUsers.B, proposal.Id)).Status);
        Assert.Equal(ProposedActionResultStatus.NotFound, (await Reject(TestUsers.B, proposal.Id)).Status);
        Assert.Equal(ProposedActionResultStatus.NotFound, (await Approve(TestUsers.A, Guid.CreateVersion7())).Status);

        Assert.Equal(ProposedActionStatus.Pending, Stored(proposal.Id).Status);
        Assert.Equal(1, _budgets.Sets);
    }

    [Fact]
    public async Task GetForReview_ReturnsTheLatestProposal_OrNone_AndHidesOtherUsersReviews()
    {
        var reviews = new InMemoryWeeklyReviewRepository();
        var review = WeeklyReview.Create(TestUsers.A, new DateOnly(2026, 10, 4), "Europe/Rome", Now, new WeeklyReviewSnapshot(
            new WeeklyFinanceSummary([]), new WeeklyGymSummary(0, 0, 0, 0, []), new WeeklyNutritionSummary(0, 0, 0, 0, 0, 0, 0, 0, [])));
        reviews.Reviews.Add(review);
        var handler = new GetReviewProposedActionHandler(reviews, _proposals);

        var none = await handler.HandleAsync(TestUsers.A, review.Id, default);
        await SetBudgetAsync(TestUsers.A, 400m);
        var proposal = await AddPendingAsync(TestUsers.A, review.Id);
        var some = await handler.HandleAsync(TestUsers.A, review.Id, default);
        var foreign = await handler.HandleAsync(TestUsers.B, review.Id, default);

        Assert.Equal((ProposedActionResultStatus.Ok, (ProposedAction?)null), (none.Status, none.Proposal));
        Assert.Equal(proposal.Id, some.Proposal!.Id);
        Assert.Equal(ProposedActionResultStatus.NotFound, foreign.Status);
    }

    // ---- Helpers ----

    private Task<ProposedActionResult> Approve(Guid userId, Guid proposalId) =>
        new ApproveProposedActionHandler(_proposals, _budgets, new SetMonthlyBudgetHandler(_budgets), _unitOfWork, _clock)
            .HandleAsync(userId, proposalId, CancellationToken.None);

    private Task<ProposedActionResult> Reject(Guid userId, Guid proposalId) =>
        new RejectProposedActionHandler(_proposals, _clock).HandleAsync(userId, proposalId, CancellationToken.None);

    private ProposedAction Stored(Guid id) => _proposals.Proposals.Single(proposal => proposal.Id == id);

    private Task SetBudgetAsync(Guid userId, decimal amount) =>
        _budgets.SetAsync(MonthlyBudget.Create(userId, 2026, 10, "EUR", amount), default);

    private async Task<ProposedAction> AddPendingAsync(Guid userId, Guid? reviewId = null)
    {
        var proposal = ProposedAction.ProposeBudgetAdjustment(userId, reviewId ?? Guid.CreateVersion7(),
            new MonthlyBudgetAdjustment(2026, 10, "EUR", 400m, 500m), "You spent 380 of 400 EUR.",
            new AgentRunIdentity("fake", "fake-model", "action-agent-v1", ActionAgentTools.ToolSchemaVersion, [ActionAgentTools.GetBudgetStatus], 2),
            _clock.GetUtcNow());
        Assert.True(await _proposals.TryAddAsync(proposal, default));

        return proposal;
    }

    // The in-memory budget store, counting writes.
    private sealed class CountingBudgetRepository : IMonthlyBudgetRepository
    {
        private readonly InMemoryMonthlyBudgetRepository _inner = new();

        public int Sets { get; private set; }

        // Approval checks the budget through the row-locking read only (the lock itself: PostgreSQL tests).
        public int LockedReads { get; private set; }

        public Task<MonthlyBudget?> GetAsync(Guid userId, int year, int month, string currency, CancellationToken cancellationToken) =>
            _inner.GetAsync(userId, year, month, currency, cancellationToken);

        public Task<MonthlyBudget?> GetForUpdateAsync(Guid userId, int year, int month, string currency, CancellationToken cancellationToken)
        {
            LockedReads++;
            return _inner.GetForUpdateAsync(userId, year, month, currency, cancellationToken);
        }

        public Task SetAsync(MonthlyBudget budget, CancellationToken cancellationToken)
        {
            Sets++;
            return _inner.SetAsync(budget, cancellationToken);
        }

        public Task DeleteAsync(Guid userId, int year, int month, string currency, CancellationToken cancellationToken) =>
            _inner.DeleteAsync(userId, year, month, currency, cancellationToken);
    }
}
