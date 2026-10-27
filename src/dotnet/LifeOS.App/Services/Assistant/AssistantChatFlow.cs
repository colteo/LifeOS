using LifeOS.Contracts.Assistant;

namespace LifeOS.App.Services.Assistant;

// The message being sent: its idempotency key is kept until the API has stored it, so resending after a lost
// answer (timeout, network) reuses the same key and never stores the message twice.
public sealed record AssistantPendingMessage(Guid ClientMessageId, string Text);

// AI-007 F4: one conversation of LifeOS Assistant on screen. The API is the only memory: the messages and the open
// draft are reloaded from it after every send, retry and draft action, so reopening the conversation shows exactly
// the same state. The flow only sends what the user asked for; confirming and cancelling are explicit actions on
// the draft (never chat text) and one action at a time. Plain .NET, no MAUI; message text and draft values live
// only in this object (page state), never persisted or logged. `changed` lets the page re-render between calls.
public sealed class AssistantChatFlow(AssistantApiClient api, Guid conversationId, Action? changed = null)
{
	// The API's limit (AssistantMessage.MaxUserContentLength); the API stays authoritative.
	public const int MaxMessageLength = 1000;

	public const string TextRequiredMessage = "Write a message.";
	public const string RecordedNotice = "Transaction recorded.";
	public const string CancelledNotice = "Draft cancelled. Nothing was recorded.";

	public Guid ConversationId { get; } = conversationId;

	public string Text { get; set; } = "";

	public bool IsLoading { get; private set; }

	public bool IsLoaded { get; private set; }

	// Loading the conversation failed (e.g. not found, or the API is unreachable): nothing else is shown.
	public string? LoadError { get; private set; }

	public IReadOnlyList<AssistantMessageResponse> Messages { get; private set; } = [];

	// The open draft, or the one the user just confirmed or cancelled here (shown with its final state).
	public AssistantOperationResponse? Operation { get; private set; }

	public AssistantPendingMessage? Pending { get; private set; }

	// A send or a retry is waiting for the assistant's turn.
	public bool IsSending { get; private set; }

	public string? ValidationMessage { get; private set; }

	public string? SendError { get; private set; }

	// A draft action (save, confirm, cancel) is in flight: every draft action is disabled meanwhile.
	public bool IsOperationBusy { get; private set; }

	public string? OperationError { get; private set; }

	public string? OperationNotice { get; private set; }

	// The draft can still be acted on: Collecting or AwaitingConfirmation.
	public bool IsOperationOpen => Operation is { Status: AssistantOperationStatuses.Collecting or AssistantOperationStatuses.AwaitingConfirmation };

	// Draft actions wait while a draft action or a turn is in flight: the turn may be changing this very draft, and a
	// confirmation in the middle would only make the turn fail (draft_changed). The draft itself stays on screen.
	public bool IsDraftLocked => IsOperationBusy || IsSending;

	public int TextLength => Text.Trim().Length;

	// The last user message, when its turn failed: the only one the API lets the user retry.
	public AssistantMessageResponse? RetryableMessage =>
		Messages.LastOrDefault(message => message.Role == "User") is { Turn.Status: AssistantTurnStatuses.Failed } last
		&& Pending is null
			? last
			: null;

	// A turn the API is still processing (e.g. the conversation was reopened while the reply was being prepared).
	public bool HasProcessingTurn => Messages.Any(message => message.Turn?.Status == AssistantTurnStatuses.Processing);

	public static string? Validate(string? text)
	{
		var value = text?.Trim();

		if (string.IsNullOrEmpty(value))
		{
			return TextRequiredMessage;
		}

		return value.Length > MaxMessageLength
			? $"The message must be at most {MaxMessageLength} characters ({value.Length} now)."
			: null;
	}

	public async Task LoadAsync(CancellationToken cancellationToken = default)
	{
		IsLoading = true;
		LoadError = null;
		changed?.Invoke();

		try
		{
			LoadError = await RefreshAsync(cancellationToken);
			IsLoaded = LoadError is null;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		finally
		{
			IsLoading = false;
		}
	}

	// Send (and resend of an unconfirmed message): the composer is cleared at once and the message is shown as
	// pending until the API has stored it.
	public async Task SendAsync(CancellationToken cancellationToken = default)
	{
		if (IsSending || IsOperationBusy || Pending is not null)
		{
			return;
		}

		ValidationMessage = Validate(Text);

		if (ValidationMessage is not null)
		{
			return;
		}

		Pending = new AssistantPendingMessage(Guid.CreateVersion7(), Text.Trim());
		Text = "";
		await SendPendingAsync(cancellationToken);
	}

	// Sends the pending message again with the same idempotency key.
	public Task ResendAsync(CancellationToken cancellationToken = default) =>
		IsSending || Pending is null ? Task.CompletedTask : SendPendingAsync(cancellationToken);

	// Gives up a message the API refused (e.g. invalid text): its text goes back to the composer.
	public void EditPending()
	{
		if (IsSending || Pending is null)
		{
			return;
		}

		Text = Pending.Text;
		Pending = null;
		SendError = null;
	}

	// The user's retry of the last failed turn: a new attempt of the same message, never a new message.
	public async Task RetryAsync(CancellationToken cancellationToken = default)
	{
		if (IsSending || RetryableMessage is not { } message)
		{
			return;
		}

		await RunTurnAsync(() => api.RetryMessageAsync(message.Id, cancellationToken), cancellationToken);
	}

	public async Task RefreshTurnAsync(CancellationToken cancellationToken = default)
	{
		if (IsSending)
		{
			return;
		}

		try
		{
			SendError = await RefreshAsync(cancellationToken);
			changed?.Invoke();
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
	}

	// ---- Draft actions ----

	// A user edit of the draft. Null request: nothing changed, nothing is sent.
	public Task<bool> SaveDraftAsync(UpdateAssistantOperationRequest? request, CancellationToken cancellationToken = default) =>
		request is null || Operation is not { } operation
			? Task.FromResult(request is null)
			: RunOperationAsync(() => api.UpdateOperationAsync(operation.Id, request, cancellationToken), value => value, null, cancellationToken);

	// Explicit confirmation of the version the user saw. A second tap while it runs does nothing, and a repeated
	// request is answered by the API with the same record (transaction, water intake or meal).
	public Task<bool> ConfirmAsync(CancellationToken cancellationToken = default) =>
		Operation is not { CanConfirm: true } operation
			? Task.FromResult(false)
			: RunOperationAsync(() => api.ConfirmOperationAsync(operation.Id, operation.DraftVersion, cancellationToken),
				value => value.Operation, AssistantDisplay.RecordedNotice(operation.Kind), cancellationToken);

	public Task<bool> CancelAsync(CancellationToken cancellationToken = default) =>
		Operation is not { Status: AssistantOperationStatuses.Collecting or AssistantOperationStatuses.AwaitingConfirmation } operation
			? Task.FromResult(false)
			: RunOperationAsync(() => api.CancelOperationAsync(operation.Id, operation.DraftVersion, cancellationToken),
				value => value, CancelledNotice, cancellationToken);

	// Hides a draft that has ended (recorded, cancelled or expired); an open draft is never hidden.
	public void DismissOperation()
	{
		if (Operation is null || IsOperationOpen || IsOperationBusy)
		{
			return;
		}

		Operation = null;
		OperationError = null;
		OperationNotice = null;
	}

	private async Task SendPendingAsync(CancellationToken cancellationToken)
	{
		var pending = Pending!;
		await RunTurnAsync(() => api.SendMessageAsync(ConversationId, pending.ClientMessageId, pending.Text, cancellationToken), cancellationToken);
	}

	private async Task RunTurnAsync(Func<Task<AssistantApiResult<AssistantTurnResponse>>> call, CancellationToken cancellationToken)
	{
		IsSending = true;
		SendError = null;
		ValidationMessage = null;
		changed?.Invoke();

		try
		{
			var result = await call();

			// Whatever happened, the stored state decides what is shown: the message, its turn and the draft.
			var refreshError = await RefreshAsync(cancellationToken);
			SendError = result.IsSuccess ? refreshError : result.Error;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// The page was left: the API keeps what it stored; reopening reloads it.
		}
		finally
		{
			IsSending = false;
			changed?.Invoke();
		}
	}

	private async Task<bool> RunOperationAsync<T>(Func<Task<AssistantApiResult<T>>> call, Func<T, AssistantOperationResponse> operationOf,
		string? notice, CancellationToken cancellationToken)
		where T : class
	{
		if (IsDraftLocked)
		{
			return false;
		}

		IsOperationBusy = true;
		OperationError = null;
		OperationNotice = null;
		changed?.Invoke();

		try
		{
			var result = await call();

			if (result.IsSuccess)
			{
				Operation = operationOf(result.Value!);
				OperationNotice = notice;
				return true;
			}

			OperationError = result.Error;

			// The draft changed, ended or was recorded elsewhere: show what is stored now.
			if (result.Code is AssistantErrorCodes.VersionConflict or AssistantErrorCodes.OperationExecuted
				or AssistantErrorCodes.OperationCancelled or AssistantErrorCodes.OperationExpired
				&& Operation is { } operation
				&& await api.GetOperationAsync(operation.Id, cancellationToken) is { IsSuccess: true } current)
			{
				Operation = current.Value;
			}

			return false;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return false;
		}
		finally
		{
			IsOperationBusy = false;
			changed?.Invoke();
		}
	}

	// Reloads the messages and the open draft. A pending message the API has stored is no longer pending. Returns
	// the error, if any (the previous state stays on screen).
	private async Task<string?> RefreshAsync(CancellationToken cancellationToken)
	{
		var detail = await api.GetConversationAsync(ConversationId, cancellationToken);

		if (!detail.IsSuccess)
		{
			return detail.Error;
		}

		var messages = await api.GetMessagesAsync(ConversationId, cancellationToken: cancellationToken);

		if (!messages.IsSuccess)
		{
			return messages.Error;
		}

		Messages = messages.Value!;

		if (Pending is { } pending && Messages.Any(message => message.ClientMessageId == pending.ClientMessageId))
		{
			Pending = null;
		}

		// A draft confirmed or cancelled here stays visible with its final state until another one opens. A draft
		// shown as open that the API no longer lists (confirmed or cancelled elsewhere, or expired) is read once to
		// show how it ended, instead of vanishing or keeping actions the API would refuse.
		if (detail.Value!.ActiveOperation is { } active)
		{
			if (active.Id != Operation?.Id)
			{
				OperationError = null;
				OperationNotice = null;
			}

			Operation = active;
		}
		else if (IsOperationOpen)
		{
			var ended = await api.GetOperationAsync(Operation!.Id, cancellationToken);
			Operation = ended.IsSuccess ? ended.Value : null;
		}

		return null;
	}
}
