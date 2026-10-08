using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Domain.ActionAgent;

// AI-002: the only action type the agent may propose (allowlist). A new type is a new enum value, a new
// payload record and a new executor; nothing executes an unknown type.
public enum ProposedActionType
{
    MonthlyBudgetAdjustment
}

// The one state machine of a proposal:
//
//   Pending ──approve──▶ Approved ──execute──▶ Executed
//      │                     └──────fail─────▶ Failed
//      └──reject──▶ Rejected
//
// Pending is the only state the AI can create. Only the user moves it on (approve/reject); only the
// deterministic executor moves an Approved proposal to Executed or Failed. Rejected, Executed and
// Failed are final.
public enum ProposedActionStatus
{
    Pending,
    Approved,
    Rejected,
    Executed,
    Failed
}

// Payload version 1 of MonthlyBudgetAdjustment: exactly the input of the existing SetMonthlyBudget
// command (year, month, currency, amount) plus the amount LifeOS read when the proposal was made.
// CurrentAmount is never model output: it comes from the get_budget_status tool, executed by LifeOS. It
// shows the user what changes, and lets execution refuse a budget that changed since.
public sealed record MonthlyBudgetAdjustment(int Year, int Month, string Currency, decimal CurrentAmount, decimal ProposedAmount);

// Technical identity of the agent run that produced a proposal: which provider, model, prompt and tool
// schema, which tools were called (fixed names, in order) and how many model steps it took. Never the
// prompt text, tool results or model reasoning.
public sealed record AgentRunIdentity(
    string Provider,
    string Model,
    string PromptVersion,
    string ToolSchemaVersion,
    IReadOnlyList<string> ToolCalls,
    int StepCount);

// AI-002: an action proposed by the Action Agent, owned by one user and tied to the weekly review it was
// derived from. The payload and rationale are immutable after creation; only the status (and its
// timestamps / failure code) changes, and only along the state machine above.
public sealed class ProposedAction
{
    public const int CurrentPayloadVersion = 1;
    public const int MaxRationaleLength = 300;
    public const int MaxIdentityLength = 100;
    public const int MaxToolCalls = 8;
    public const int MaxSteps = 8;
    public const int MaxFailureCodeLength = 64;

    // Agent policy (not a Finance rule): one suggestion changes a budget by at most this factor in either
    // direction. Larger changes are left to the user.
    public const decimal MaxChangeFactor = 2m;

    // Execution failures (stable codes; never exception text).
    public const string BudgetChanged = "budget_changed";
    public const string BudgetRejected = "budget_rejected";

    private ProposedAction(
        Guid id,
        Guid userId,
        Guid reviewId,
        ProposedActionType actionType,
        int payloadVersion,
        MonthlyBudgetAdjustment payload,
        string rationale,
        AgentRunIdentity run,
        ProposedActionStatus status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? decidedAtUtc,
        DateTimeOffset? executedAtUtc,
        string? failureCode)
    {
        Id = id;
        UserId = userId;
        ReviewId = reviewId;
        ActionType = actionType;
        PayloadVersion = payloadVersion;
        Payload = payload;
        Rationale = rationale;
        Run = run;
        Status = status;
        CreatedAtUtc = createdAtUtc;
        DecidedAtUtc = decidedAtUtc;
        ExecutedAtUtc = executedAtUtc;
        FailureCode = failureCode;
    }

    public Guid Id { get; }

    public Guid UserId { get; }

    // The saved weekly review the agent analysed (source context).
    public Guid ReviewId { get; }

    public ProposedActionType ActionType { get; }

    public int PayloadVersion { get; }

    public MonthlyBudgetAdjustment Payload { get; }

    public string Rationale { get; }

    public AgentRunIdentity Run { get; }

    public ProposedActionStatus Status { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    // When the user approved or rejected it.
    public DateTimeOffset? DecidedAtUtc { get; }

    public DateTimeOffset? ExecutedAtUtc { get; }

    // Failed only: why execution did not happen (BudgetChanged, BudgetRejected).
    public string? FailureCode { get; }

    // Pending or Approved: a decision or an execution is still to come.
    public bool IsOpen => Status is ProposedActionStatus.Pending or ProposedActionStatus.Approved;

    // A new Pending proposal. Throws ArgumentException for anything outside the contract, including the
    // agent policy (a real change, within MaxChangeFactor of the current budget).
    public static ProposedAction ProposeBudgetAdjustment(
        Guid userId,
        Guid reviewId,
        MonthlyBudgetAdjustment adjustment,
        string rationale,
        AgentRunIdentity run,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(adjustment);

        var normalized = adjustment with { Currency = MonthlyBudget.NormalizeCurrency(adjustment.Currency) };

        if (normalized.ProposedAmount == normalized.CurrentAmount)
        {
            throw new ArgumentException("The proposed amount must differ from the current budget.", nameof(adjustment));
        }

        if (normalized.ProposedAmount > normalized.CurrentAmount * MaxChangeFactor
            || normalized.ProposedAmount * MaxChangeFactor < normalized.CurrentAmount)
        {
            throw new ArgumentException($"A suggestion changes a budget by at most a factor of {MaxChangeFactor}.", nameof(adjustment));
        }

        return Restore(Guid.CreateVersion7(), userId, reviewId, ProposedActionType.MonthlyBudgetAdjustment, CurrentPayloadVersion,
            normalized, rationale, run, ProposedActionStatus.Pending, createdAtUtc, null, null, null);
    }

    // Rebuilds a stored proposal (persistence only): the same payload invariants plus the state shape.
    public static ProposedAction Restore(
        Guid id,
        Guid userId,
        Guid reviewId,
        ProposedActionType actionType,
        int payloadVersion,
        MonthlyBudgetAdjustment payload,
        string rationale,
        AgentRunIdentity run,
        ProposedActionStatus status,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? decidedAtUtc,
        DateTimeOffset? executedAtUtc,
        string? failureCode)
    {
        if (id == Guid.Empty || userId == Guid.Empty || reviewId == Guid.Empty)
        {
            throw new ArgumentException("A proposal id, a user id and a review id are required.");
        }

        if (!Enum.IsDefined(actionType))
        {
            throw new ArgumentOutOfRangeException(nameof(actionType), actionType, "Unsupported action type.");
        }

        if (payloadVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadVersion), payloadVersion, "The payload version must be at least 1.");
        }

        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(run);
        MonthlyBudget.ValidateMonth(payload.Year, payload.Month);
        var currency = MonthlyBudget.NormalizeCurrency(payload.Currency);
        Amount(payload.CurrentAmount, nameof(payload.CurrentAmount));
        Amount(payload.ProposedAmount, nameof(payload.ProposedAmount));

        var identity = new AgentRunIdentity(
            Text(run.Provider, MaxIdentityLength, nameof(run.Provider)),
            Text(run.Model, MaxIdentityLength, nameof(run.Model)),
            Text(run.PromptVersion, MaxIdentityLength, nameof(run.PromptVersion)),
            Text(run.ToolSchemaVersion, MaxIdentityLength, nameof(run.ToolSchemaVersion)),
            ToolCalls(run.ToolCalls),
            run.StepCount is >= 1 and <= MaxSteps
                ? run.StepCount
                : throw new ArgumentOutOfRangeException(nameof(run), run.StepCount, $"The step count must be between 1 and {MaxSteps}."));

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown status.");
        }

        ValidateShape(status, decidedAtUtc, executedAtUtc, failureCode);

        return new ProposedAction(id, userId, reviewId, actionType, payloadVersion, payload with { Currency = currency },
            Text(rationale, MaxRationaleLength, nameof(rationale)), identity, status, createdAtUtc.ToUniversalTime(),
            decidedAtUtc?.ToUniversalTime(), executedAtUtc?.ToUniversalTime(), failureCode);
    }

    public static bool CanTransition(ProposedActionStatus from, ProposedActionStatus to) => (from, to) switch
    {
        (ProposedActionStatus.Pending, ProposedActionStatus.Approved) => true,
        (ProposedActionStatus.Pending, ProposedActionStatus.Rejected) => true,
        (ProposedActionStatus.Approved, ProposedActionStatus.Executed) => true,
        (ProposedActionStatus.Approved, ProposedActionStatus.Failed) => true,
        _ => false
    };

    // The user's explicit approval. Execution is a separate step (Approved → Executed/Failed).
    public ProposedAction Approve(DateTimeOffset atUtc) => With(ProposedActionStatus.Approved, atUtc, null, null);

    public ProposedAction Reject(DateTimeOffset atUtc) => With(ProposedActionStatus.Rejected, atUtc, null, null);

    public ProposedAction MarkExecuted(DateTimeOffset atUtc) => With(ProposedActionStatus.Executed, DecidedAtUtc, atUtc, null);

    public ProposedAction MarkFailed(string failureCode) => With(ProposedActionStatus.Failed, DecidedAtUtc, null, failureCode);

    private ProposedAction With(ProposedActionStatus to, DateTimeOffset? decidedAtUtc, DateTimeOffset? executedAtUtc, string? failureCode)
    {
        if (!CanTransition(Status, to))
        {
            throw new InvalidOperationException($"A {Status} proposal cannot become {to}.");
        }

        ValidateShape(to, decidedAtUtc, executedAtUtc, failureCode);

        return new ProposedAction(Id, UserId, ReviewId, ActionType, PayloadVersion, Payload, Rationale, Run, to, CreatedAtUtc,
            decidedAtUtc?.ToUniversalTime(), executedAtUtc?.ToUniversalTime(), failureCode);
    }

    // Which timestamps / failure code each status carries (also a database check).
    private static void ValidateShape(ProposedActionStatus status, DateTimeOffset? decidedAtUtc, DateTimeOffset? executedAtUtc, string? failureCode)
    {
        var valid = status switch
        {
            ProposedActionStatus.Pending => decidedAtUtc is null && executedAtUtc is null && failureCode is null,
            ProposedActionStatus.Approved or ProposedActionStatus.Rejected => decidedAtUtc is not null && executedAtUtc is null && failureCode is null,
            ProposedActionStatus.Executed => decidedAtUtc is not null && executedAtUtc is not null && failureCode is null,
            ProposedActionStatus.Failed => decidedAtUtc is not null && executedAtUtc is null
                && !string.IsNullOrWhiteSpace(failureCode) && failureCode.Length <= MaxFailureCodeLength,
            _ => false
        };

        if (!valid)
        {
            throw new ArgumentException($"Invalid timestamps or failure code for a {status} proposal.", nameof(status));
        }
    }

    private static void Amount(decimal amount, string field)
    {
        if (amount <= 0 || amount > Transaction.MaxAmount || decimal.Round(amount, Transaction.MaxDecimalPlaces) != amount)
        {
            throw new ArgumentException("Amounts must be positive, within the monetary range and have at most 4 decimal places.", field);
        }
    }

    private static IReadOnlyList<string> ToolCalls(IReadOnlyList<string>? calls)
    {
        if (calls is null || calls.Count > MaxToolCalls)
        {
            throw new ArgumentException($"At most {MaxToolCalls} tool calls are recorded.", nameof(calls));
        }

        return calls.Select(call => Text(call, MaxIdentityLength, nameof(calls))).ToList();
    }

    // Non-empty single-line plain text within the limit, trimmed.
    private static string Text(string? value, int maxLength, string field)
    {
        var text = value?.Trim();

        if (string.IsNullOrEmpty(text) || text.Length > maxLength || text.Any(char.IsControl))
        {
            throw new ArgumentException($"{field} must be one line of 1–{maxLength} characters.", field);
        }

        return text;
    }
}
