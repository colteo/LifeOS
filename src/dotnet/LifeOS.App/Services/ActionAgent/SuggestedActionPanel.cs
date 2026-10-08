using System.Globalization;
using LifeOS.App.Services.Finance;
using LifeOS.Contracts.ActionAgent;

namespace LifeOS.App.Services.ActionAgent;

public enum SuggestedActionState
{
	// Reading the review's latest suggestion (no AI call).
	Checking,

	// Never analysed.
	None,

	// Waiting for the assistant (AI calls).
	Analyzing,

	// The assistant found nothing worth changing (Message says why).
	NoAction,

	// A proposal is shown; its own Status says whether it awaits a decision or is final.
	Proposal,

	// Sending the user's decision (apply / not now).
	Deciding,

	// Reading, analysing or deciding failed.
	Error
}

// AI-002: the state of the "Suggested action" card of one weekly review. Independent of the review and
// of the AI Insights: whatever happens here, they stay as they are. Plain .NET, no MAUI.
public sealed class SuggestedActionPanel(SuggestedActionApiClient api)
{
	private enum Step { Load, Analyze, Decide }

	private Step _failed;
	private Guid _reviewId;

	public SuggestedActionState State { get; private set; } = SuggestedActionState.Checking;

	public ProposedActionResponse? Proposal { get; private set; }

	public string? Message { get; private set; }

	public string? ErrorMessage { get; private set; }

	public bool IsBusy => State is SuggestedActionState.Checking or SuggestedActionState.Analyzing or SuggestedActionState.Deciding;

	// Apply is offered only while the proposal awaits execution; "Not now" only while it is undecided.
	public bool CanApply => State == SuggestedActionState.Proposal
		&& Proposal?.Status is ProposedActionStatuses.Pending or ProposedActionStatuses.Approved;

	public bool CanReject => State == SuggestedActionState.Proposal && Proposal?.Status == ProposedActionStatuses.Pending;

	// A new analysis may be asked for once nothing is open.
	public bool CanAnalyze => State is SuggestedActionState.None or SuggestedActionState.NoAction
		|| (State == SuggestedActionState.Proposal && !CanApply);

	public async Task LoadAsync(Guid reviewId, CancellationToken cancellationToken = default)
	{
		_reviewId = reviewId;
		Start(SuggestedActionState.Checking);
		Apply(await api.GetForReviewAsync(reviewId, cancellationToken), Step.Load);
	}

	public async Task AnalyzeAsync(CancellationToken cancellationToken = default)
	{
		if (IsBusy)
		{
			return;
		}

		Start(SuggestedActionState.Analyzing);
		Apply(await api.AnalyzeAsync(_reviewId, cancellationToken), Step.Analyze);
	}

	public Task ApproveAsync(CancellationToken cancellationToken = default) =>
		CanApply ? DecideAsync(api.ApproveAsync(Proposal!.Id, cancellationToken)) : Task.CompletedTask;

	public Task RejectAsync(CancellationToken cancellationToken = default) =>
		CanReject ? DecideAsync(api.RejectAsync(Proposal!.Id, cancellationToken)) : Task.CompletedTask;

	// Repeats a failed read or analysis. After a failed decision it reads the stored state again
	// (the decision may have happened, or been made impossible, meanwhile); it never re-sends it.
	public Task RetryAsync(CancellationToken cancellationToken = default) =>
		_failed == Step.Analyze ? AnalyzeAsync(cancellationToken) : LoadAsync(_reviewId, cancellationToken);

	private async Task DecideAsync(Task<ApiResult<ProposedActionResponse>> decision)
	{
		var proposal = Proposal;
		State = SuggestedActionState.Deciding;
		ErrorMessage = null;

		var result = await decision;

		if (result.IsSuccess && result.Value is { } decided)
		{
			Proposal = decided;
			State = SuggestedActionState.Proposal;
			return;
		}

		Proposal = proposal;
		Fail(result.Errors, Step.Decide);
	}

	private void Start(SuggestedActionState state)
	{
		State = state;
		Proposal = null;
		Message = null;
		ErrorMessage = null;
	}

	private void Apply(ApiResult<SuggestedActionStateResponse> result, Step step)
	{
		if (!result.IsSuccess || result.Value is not { } value)
		{
			Fail(result.Errors, step);
			return;
		}

		switch (value)
		{
			case { Status: SuggestedActionStatuses.Proposal, Proposal: { } proposal }:
				State = SuggestedActionState.Proposal;
				Proposal = proposal;
				break;

			case { Status: SuggestedActionStatuses.NoAction }:
				State = SuggestedActionState.NoAction;
				Message = value.Message;
				break;

			default:
				State = SuggestedActionState.None;
				break;
		}
	}

	private void Fail(IReadOnlyList<string> errors, Step step)
	{
		State = SuggestedActionState.Error;
		ErrorMessage = errors.Count > 0 ? string.Join(" ", errors) : SuggestedActionDisplay.FallbackError;
		_failed = step;
	}
}

// Wording of the card. The API's proposal is authoritative for every value; these only decide words.
public static class SuggestedActionDisplay
{
	public const string Title = "Suggested action";

	public const string Intro = "Ask the assistant whether a monthly budget should change, based on this week. Nothing changes unless you apply it.";

	public const string NoActionFallback = "No budget change suggested.";

	public const string ApprovalNote = "Nothing changes until you tap Apply.";

	public const string FallbackError = "The suggestion could not be loaded. Try again later.";

	private static readonly DateTimeFormatInfo Months = CultureInfo.InvariantCulture.DateTimeFormat;

	// "Change your October 2026 EUR budget".
	public static string Heading(MonthlyBudgetAdjustmentResponse adjustment) =>
		$"Change your {Months.GetMonthName(adjustment.Month)} {adjustment.Year} {adjustment.Currency} budget";

	// "400.00 EUR → 500.00 EUR".
	public static string Change(MonthlyBudgetAdjustmentResponse adjustment, CultureInfo? culture = null) =>
		$"{AnalyticsDisplay.Amount(adjustment.CurrentAmount, adjustment.Currency, culture)} → {AnalyticsDisplay.Amount(adjustment.ProposedAmount, adjustment.Currency, culture)}";

	// "+100.00 EUR" / "−50.00 EUR".
	public static string Difference(MonthlyBudgetAdjustmentResponse adjustment, CultureInfo? culture = null) =>
		AnalyticsDisplay.NetFlow(adjustment.ProposedAmount - adjustment.CurrentAmount, adjustment.Currency, culture);

	// The outcome line of a decided proposal; null while it awaits a decision.
	public static string? Outcome(ProposedActionResponse proposal, CultureInfo? culture = null) => proposal.Status switch
	{
		ProposedActionStatuses.Executed =>
			$"Applied. The budget is now {AnalyticsDisplay.Amount(proposal.Adjustment.ProposedAmount, proposal.Adjustment.Currency, culture)}.",
		ProposedActionStatuses.Rejected => "Dismissed. Nothing was changed.",
		ProposedActionStatuses.Failed when proposal.FailureCode == "budget_changed" =>
			"Not applied: the budget changed after this suggestion. Nothing was changed.",
		ProposedActionStatuses.Failed => "Not applied. Nothing was changed.",
		ProposedActionStatuses.Approved => "Approved but not applied yet. Tap Apply to finish.",
		_ => null
	};

	public static string NoAction(string? message) => string.IsNullOrWhiteSpace(message) ? NoActionFallback : message;

	// "Suggested by AI (groq · openai/gpt-oss-20b · action-agent-v1). You decide."
	public static string Attribution(ProposedActionResponse proposal) =>
		$"Suggested by AI ({proposal.Provider} · {proposal.Model} · {proposal.PromptVersion}). You decide.";
}
