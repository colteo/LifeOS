using LifeOS.Domain.ActionAgent;

namespace LifeOS.UnitTests.ActionAgent;

// AI-002: the ProposedAction invariants (allowlisted type, bounded payload, agent policy) and its one
// state machine.
public class ProposedActionDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly MonthlyBudgetAdjustment Adjustment = new(2026, 10, "eur", 400m, 500m);
    private static readonly AgentRunIdentity Run = new("fake", "fake-model", "action-agent-v1", "action-agent-tools-v1",
        ["get_weekly_review", "get_budget_status"], 3);

    private static readonly Guid Owner = Guid.CreateVersion7();

    private static ProposedAction Pending() =>
        ProposedAction.ProposeBudgetAdjustment(Owner, Guid.CreateVersion7(), Adjustment, " Raise it. ", Run, Now);

    [Fact]
    public void Propose_CreatesAPendingNormalizedProposal()
    {
        var proposal = Pending();

        Assert.Equal(ProposedActionStatus.Pending, proposal.Status);
        Assert.Equal(ProposedActionType.MonthlyBudgetAdjustment, proposal.ActionType);
        Assert.Equal((1, "EUR", "Raise it."), (proposal.PayloadVersion, proposal.Payload.Currency, proposal.Rationale));
        Assert.Equal((Now, (DateTimeOffset?)null, (DateTimeOffset?)null, (string?)null),
            (proposal.CreatedAtUtc, proposal.DecidedAtUtc, proposal.ExecutedAtUtc, proposal.FailureCode));
        Assert.True(proposal.IsOpen);
    }

    public static TheoryData<MonthlyBudgetAdjustment> InvalidAdjustments => new()
    {
        Adjustment with { ProposedAmount = 400m },          // no change
        Adjustment with { ProposedAmount = 800.01m },       // more than double
        Adjustment with { ProposedAmount = 199.99m },       // less than half
        Adjustment with { ProposedAmount = 0m },
        Adjustment with { ProposedAmount = 450.00001m },    // more than 4 decimals
        Adjustment with { CurrentAmount = 0m },
        Adjustment with { Month = 13 },
        Adjustment with { Currency = "EURO" }
    };

    [Theory]
    [MemberData(nameof(InvalidAdjustments))]
    public void Propose_RejectsAnythingOutsideTheContractOrThePolicy(MonthlyBudgetAdjustment adjustment)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            ProposedAction.ProposeBudgetAdjustment(Owner, Guid.CreateVersion7(), adjustment, "Why.", Run, Now));
    }

    [Theory]
    [InlineData(800)]
    [InlineData(200)]
    public void Propose_AcceptsTheChangeFactorLimits(decimal amount)
    {
        Assert.Equal(amount, ProposedAction.ProposeBudgetAdjustment(Owner, Guid.CreateVersion7(),
            Adjustment with { ProposedAmount = amount }, "Why.", Run, Now).Payload.ProposedAmount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Two\nlines.")]
    public void Propose_RequiresAShortSingleLineRationale(string rationale)
    {
        Assert.Throws<ArgumentException>(() =>
            ProposedAction.ProposeBudgetAdjustment(Owner, Guid.CreateVersion7(), Adjustment, rationale, Run, Now));
        Assert.Throws<ArgumentException>(() =>
            ProposedAction.ProposeBudgetAdjustment(Owner, Guid.CreateVersion7(), Adjustment, new string('r', 301), Run, Now));
    }

    [Fact]
    public void Propose_RequiresTheRunIdentity()
    {
        Assert.Throws<ArgumentException>(() => ProposedAction.ProposeBudgetAdjustment(Owner, Guid.CreateVersion7(), Adjustment, "Why.",
            Run with { Model = " " }, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProposedAction.ProposeBudgetAdjustment(Owner, Guid.CreateVersion7(), Adjustment, "Why.",
            Run with { StepCount = 0 }, Now));
        Assert.Throws<ArgumentException>(() => ProposedAction.ProposeBudgetAdjustment(Guid.Empty, Guid.CreateVersion7(), Adjustment, "Why.", Run, Now));
    }

    // ---- State machine ----

    [Fact]
    public void Approve_ThenExecute_KeepsThePayloadAndRecordsBothInstants()
    {
        var pending = Pending();

        var executed = pending.Approve(Now.AddMinutes(1)).MarkExecuted(Now.AddMinutes(2));

        Assert.Equal(ProposedActionStatus.Executed, executed.Status);
        Assert.Equal((Now.AddMinutes(1), Now.AddMinutes(2)), (executed.DecidedAtUtc!.Value, executed.ExecutedAtUtc!.Value));
        Assert.Equal(pending.Payload, executed.Payload);
        Assert.False(executed.IsOpen);
    }

    [Fact]
    public void Approve_ThenFail_RecordsTheCode()
    {
        var failed = Pending().Approve(Now).MarkFailed(ProposedAction.BudgetChanged);

        Assert.Equal((ProposedActionStatus.Failed, "budget_changed"), (failed.Status, failed.FailureCode));
        Assert.Null(failed.ExecutedAtUtc);
    }

    [Fact]
    public void OnlyTheDocumentedTransitionsExist()
    {
        var allowed = new[]
        {
            (ProposedActionStatus.Pending, ProposedActionStatus.Approved),
            (ProposedActionStatus.Pending, ProposedActionStatus.Rejected),
            (ProposedActionStatus.Approved, ProposedActionStatus.Executed),
            (ProposedActionStatus.Approved, ProposedActionStatus.Failed)
        };

        foreach (var from in Enum.GetValues<ProposedActionStatus>())
        {
            foreach (var to in Enum.GetValues<ProposedActionStatus>())
            {
                Assert.Equal(allowed.Contains((from, to)), ProposedAction.CanTransition(from, to));
            }
        }
    }

    [Fact]
    public void ARejectedProposalCanNeverBeExecuted_AndPendingCannotSkipApproval()
    {
        var rejected = Pending().Reject(Now);

        Assert.Throws<InvalidOperationException>(() => rejected.Approve(Now));
        Assert.Throws<InvalidOperationException>(() => rejected.MarkExecuted(Now));
        Assert.Throws<InvalidOperationException>(() => Pending().MarkExecuted(Now));
        Assert.Throws<InvalidOperationException>(() => Pending().MarkFailed(ProposedAction.BudgetChanged));
    }

    [Fact]
    public void FinalStatesAreFinal()
    {
        var executed = Pending().Approve(Now).MarkExecuted(Now);

        Assert.Throws<InvalidOperationException>(() => executed.Approve(Now));
        Assert.Throws<InvalidOperationException>(() => executed.MarkExecuted(Now));
        Assert.Throws<InvalidOperationException>(() => executed.Reject(Now));
    }

    [Fact]
    public void Restore_RejectsAnInconsistentStateShape()
    {
        var pending = Pending();

        Assert.Throws<ArgumentException>(() => ProposedAction.Restore(pending.Id, pending.UserId, pending.ReviewId, pending.ActionType, 1,
            pending.Payload, pending.Rationale, pending.Run, ProposedActionStatus.Executed, Now, Now, null, null));
        Assert.Throws<ArgumentException>(() => ProposedAction.Restore(pending.Id, pending.UserId, pending.ReviewId, pending.ActionType, 1,
            pending.Payload, pending.Rationale, pending.Run, ProposedActionStatus.Failed, Now, Now, null, null));
        Assert.Throws<ArgumentException>(() => ProposedAction.Restore(pending.Id, pending.UserId, pending.ReviewId, pending.ActionType, 1,
            pending.Payload, pending.Rationale, pending.Run, ProposedActionStatus.Pending, Now, Now, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProposedAction.Restore(pending.Id, pending.UserId, pending.ReviewId, (ProposedActionType)7, 1,
            pending.Payload, pending.Rationale, pending.Run, ProposedActionStatus.Pending, Now, null, null, null));
    }
}
