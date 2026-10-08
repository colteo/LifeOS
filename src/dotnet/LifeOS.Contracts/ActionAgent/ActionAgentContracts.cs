namespace LifeOS.Contracts.ActionAgent;

// AI-002: the suggested action of one weekly review.
// Status: "None" (GET: never analysed, Proposal null), "Proposal" (the review's latest proposal, whatever
// its own status), "NoAction" (POST: the agent found nothing worth proposing; Message says why).
public sealed record SuggestedActionStateResponse(string Status, ProposedActionResponse? Proposal, string? Message);

public static class SuggestedActionStatuses
{
    public const string None = "None";
    public const string Proposal = "Proposal";
    public const string NoAction = "NoAction";
}

// One proposal. Status: Pending, Approved, Rejected, Executed or Failed. FailureCode (Failed only):
// "budget_changed" (the budget no longer has CurrentAmount) or "budget_rejected".
// Provider, Model and PromptVersion attribute the AI that proposed it.
public sealed record ProposedActionResponse(
    Guid Id,
    Guid ReviewId,
    string ActionType,
    string Status,
    MonthlyBudgetAdjustmentResponse Adjustment,
    string Rationale,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DecidedAtUtc,
    DateTimeOffset? ExecutedAtUtc,
    string? FailureCode,
    string Provider,
    string Model,
    string PromptVersion);

// ActionType "MonthlyBudgetAdjustment": set the monthly budget of Year/Month/Currency from CurrentAmount
// (as read when proposed) to ProposedAmount.
public sealed record MonthlyBudgetAdjustmentResponse(int Year, int Month, string Currency, decimal CurrentAmount, decimal ProposedAmount);

public static class ProposedActionStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Executed = "Executed";
    public const string Failed = "Failed";
}
