using LifeOS.Contracts.Memory;

namespace LifeOS.App.Services.Journal;

public enum JournalAskState
{
	Idle,

	// Indexing the entries the Journal Memory has not caught up with yet (bounded sync calls).
	Preparing,

	// Waiting for the grounded answer.
	Asking,

	Answered,

	InsufficientEvidence,

	// Status, sync or ask failed, or preparing stopped making progress; Retry runs the whole flow again.
	Error
}

// JRN-003: one Ask Journal question. Before asking, the user's pending entries are indexed with bounded
// POST sync calls (SyncBatchSize each), so the answer is never built from a knowingly incomplete index.
// Preparing stops (retryable error) on any failure or as soon as a sync makes no progress; it can
// never loop forever because the pending count must strictly decrease. The question, answer and
// citations live only in this object (page state): never persisted or logged. Plain .NET, no MAUI.
// `changed` lets the page re-render between the calls (Preparing progress, then Searching).
public sealed class JournalAskFlow(JournalMemoryApiClient api, Action? changed = null)
{
	public const int SyncBatchSize = 3;

	// The API's limit (JournalMemoryPolicy.MaxQuestionLength); the API stays authoritative.
	public const int MaxQuestionLength = 500;

	public const string AnsweredStatus = "answered";
	public const string InsufficientEvidenceStatus = "insufficient_evidence";

	public const string QuestionRequiredMessage = "Write a question.";
	public const string PreparationStalledMessage = "Your journal could not be fully prepared right now. Try again in a few minutes.";
	public const string UnexpectedAnswerMessage = "Ask Journal could not produce a valid answer. Try again later.";

	public string Question { get; set; } = "";

	public JournalAskState State { get; private set; } = JournalAskState.Idle;

	// Shown under the question field (the question is kept as typed).
	public string? ValidationMessage { get; private set; }

	public string? ErrorMessage { get; private set; }

	// While preparing: the entries still to index, as last reported by the API.
	public int PendingEntries { get; private set; }

	public JournalMemoryAnswerResponse? Result { get; private set; }

	public bool IsBusy => State is JournalAskState.Preparing or JournalAskState.Asking;

	public int QuestionLength => Question.Trim().Length;

	public static string? Validate(string? question)
	{
		var text = question?.Trim();

		if (string.IsNullOrEmpty(text))
		{
			return QuestionRequiredMessage;
		}

		return text.Length > MaxQuestionLength
			? $"The question must be at most {MaxQuestionLength} characters ({text.Length} now)."
			: null;
	}

	// Ask (and Retry): status → sync while pending → ask. Cancellation (the page was left) ends quietly.
	public async Task AskAsync(CancellationToken cancellationToken = default)
	{
		if (IsBusy)
		{
			return;
		}

		ValidationMessage = Validate(Question);

		if (ValidationMessage is not null)
		{
			return;
		}

		var question = Question.Trim();
		Result = null;
		ErrorMessage = null;
		PendingEntries = 0;
		State = JournalAskState.Asking;
		changed?.Invoke();

		try
		{
			if (!await PrepareAsync(cancellationToken))
			{
				return;
			}

			State = JournalAskState.Asking;
			changed?.Invoke();
			var answer = await api.AskAsync(question, cancellationToken);

			if (!answer.IsSuccess)
			{
				Fail(answer.Errors);
				return;
			}

			var value = answer.Value!;

			switch (value.Status)
			{
				case AnsweredStatus:
					Result = value;
					State = JournalAskState.Answered;
					break;
				case InsufficientEvidenceStatus:
					Result = value;
					State = JournalAskState.InsufficientEvidence;
					break;
				default:
					Fail([UnexpectedAnswerMessage]);
					break;
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			State = JournalAskState.Idle;
		}
	}

	// True when nothing is pending any more. Each sync must index (or discard as stale) at least one
	// entry and lower the pending count; otherwise preparing stops with a retryable error.
	private async Task<bool> PrepareAsync(CancellationToken cancellationToken)
	{
		var status = await api.GetStatusAsync(cancellationToken);

		if (!status.IsSuccess)
		{
			Fail(status.Errors);
			return false;
		}

		var pending = status.Value!.PendingEntries;

		while (pending > 0)
		{
			PendingEntries = pending;
			State = JournalAskState.Preparing;
			changed?.Invoke();

			var sync = await api.SyncAsync(SyncBatchSize, cancellationToken);

			if (!sync.IsSuccess)
			{
				Fail(sync.Errors);
				return false;
			}

			var progressed = sync.Value!.Indexed + sync.Value.StaleDiscarded > 0 && sync.Value.PendingRemaining < pending;

			if (!progressed)
			{
				Fail([PreparationStalledMessage]);
				return false;
			}

			pending = sync.Value.PendingRemaining;
		}

		PendingEntries = 0;
		return true;
	}

	private void Fail(IReadOnlyList<string> errors)
	{
		State = JournalAskState.Error;
		ErrorMessage = errors.Count > 0 ? string.Join(" ", errors) : JournalMemoryApiClient.UnavailableMessage;
	}
}
