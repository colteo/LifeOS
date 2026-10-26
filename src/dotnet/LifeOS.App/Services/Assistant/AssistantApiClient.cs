using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Contracts.Assistant;

namespace LifeOS.App.Services.Assistant;

// The outcome of one LifeOS Assistant API call. Code is the API's stable problem code (AssistantErrorCodes) when
// it sent one; Error is the readable message (the API's own text, or a fixed one for transport failures).
public sealed record AssistantApiResult<T>(T? Value, string? Error, string? Code, int? CurrentVersion = null)
{
	public bool IsSuccess => Error is null;

	public static AssistantApiResult<T> Success(T value) => new(value, null, null);

	public static AssistantApiResult<T> Failure(string error, string? code = null, int? currentVersion = null) =>
		new(default, error, code, currentVersion);
}

// AI-007 F4: the LifeOS Assistant API (F1 conversations and messages, F2 operations), unchanged. The owner is
// always the signed-in user (the authorized pipeline adds the token and refreshes it once on 401). Sending
// and retrying a message wait for the assistant's turn (the API calls the AI service), so they use the
// longer AI client; everything else uses the default one. Every write carries the client's idempotency key
// or the draft version the user saw. Nothing here logs or stores message text or draft values.
public sealed class AssistantApiClient
{
	public const string AssistantPath = "api/assistant";
	public const string ConversationsPath = AssistantPath + "/conversations";
	public const string MessagesPath = AssistantPath + "/messages";
	public const string OperationsPath = AssistantPath + "/operations";

	// The API's limits (AssistantPaging); the API stays authoritative.
	public const int ConversationPageSize = 20;
	public const int MessagePageSize = 200;

	public const string UnavailableMessage = "The assistant is unavailable right now. Your conversation is safe; try again later.";

	private readonly HttpClient _httpClient;
	private readonly HttpClient _turnHttpClient;

	public AssistantApiClient(HttpClient httpClient, HttpClient turnHttpClient)
	{
		_httpClient = httpClient;
		_turnHttpClient = turnHttpClient;
	}

	// ---- Conversations (F1) ----

	public Task<AssistantApiResult<AssistantConversationPageResponse>> GetConversationsAsync(string? cursor = null,
		CancellationToken cancellationToken = default) =>
		SendAsync<AssistantConversationPageResponse>(() => _httpClient.GetAsync(
			cursor is null
				? $"{ConversationsPath}?limit={ConversationPageSize}"
				: $"{ConversationsPath}?limit={ConversationPageSize}&cursor={Uri.EscapeDataString(cursor)}",
			cancellationToken), cancellationToken);

	// Repeating the call with the same clientConversationId returns the same conversation (no duplicate).
	public Task<AssistantApiResult<AssistantConversationResponse>> CreateConversationAsync(Guid clientConversationId,
		CancellationToken cancellationToken = default) =>
		SendAsync<AssistantConversationResponse>(() => _httpClient.PostAsJsonAsync(ConversationsPath,
			new CreateAssistantConversationRequest(clientConversationId), cancellationToken), cancellationToken);

	// The conversation and its open operation, if any (F2).
	public Task<AssistantApiResult<AssistantConversationDetailResponse>> GetConversationAsync(Guid conversationId,
		CancellationToken cancellationToken = default) =>
		SendAsync<AssistantConversationDetailResponse>(() => _httpClient.GetAsync($"{ConversationsPath}/{conversationId}", cancellationToken),
			cancellationToken);

	// Every message after `afterSequence`, following NextAfterSequence until the last page.
	public async Task<AssistantApiResult<IReadOnlyList<AssistantMessageResponse>>> GetMessagesAsync(Guid conversationId, int afterSequence = 0,
		CancellationToken cancellationToken = default)
	{
		var messages = new List<AssistantMessageResponse>();
		int? after = afterSequence;

		while (after is { } sequence)
		{
			var page = await SendAsync<AssistantMessagePageResponse>(() => _httpClient.GetAsync(
				$"{ConversationsPath}/{conversationId}/messages?afterSequence={sequence}&limit={MessagePageSize}", cancellationToken), cancellationToken);

			if (!page.IsSuccess)
			{
				return AssistantApiResult<IReadOnlyList<AssistantMessageResponse>>.Failure(page.Error!, page.Code);
			}

			messages.AddRange(page.Value!.Items);
			after = page.Value.NextAfterSequence is { } next && next > sequence ? next : null;
		}

		return AssistantApiResult<IReadOnlyList<AssistantMessageResponse>>.Success(messages);
	}

	// Repeating the call with the same clientMessageId and text never stores a second message.
	public Task<AssistantApiResult<AssistantTurnResponse>> SendMessageAsync(Guid conversationId, Guid clientMessageId, string text,
		CancellationToken cancellationToken = default) =>
		SendAsync<AssistantTurnResponse>(() => _turnHttpClient.PostAsJsonAsync($"{ConversationsPath}/{conversationId}/messages",
			new SendAssistantMessageRequest(clientMessageId, text), cancellationToken), cancellationToken);

	// A new attempt of the same failed message (never a new message).
	public Task<AssistantApiResult<AssistantTurnResponse>> RetryMessageAsync(Guid messageId, CancellationToken cancellationToken = default) =>
		SendAsync<AssistantTurnResponse>(() => _turnHttpClient.PostAsync($"{MessagesPath}/{messageId}/retry", null, cancellationToken),
			cancellationToken);

	// ---- Operations (F2) ----

	public Task<AssistantApiResult<AssistantOperationResponse>> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default) =>
		SendAsync<AssistantOperationResponse>(() => _httpClient.GetAsync($"{OperationsPath}/{operationId}", cancellationToken), cancellationToken);

	public Task<AssistantApiResult<AssistantOperationResponse>> UpdateOperationAsync(Guid operationId, UpdateAssistantOperationRequest request,
		CancellationToken cancellationToken = default) =>
		SendAsync<AssistantOperationResponse>(() => _httpClient.PatchAsJsonAsync($"{OperationsPath}/{operationId}", request, cancellationToken),
			cancellationToken);

	// Idempotent on the server: a repeated confirmation returns the same transaction (Replayed), never a second one.
	public Task<AssistantApiResult<AssistantOperationConfirmationResponse>> ConfirmOperationAsync(Guid operationId, int expectedVersion,
		CancellationToken cancellationToken = default) =>
		SendAsync<AssistantOperationConfirmationResponse>(() => _httpClient.PostAsJsonAsync($"{OperationsPath}/{operationId}/confirm",
			new AssistantOperationVersionRequest(expectedVersion), cancellationToken), cancellationToken);

	public Task<AssistantApiResult<AssistantOperationResponse>> CancelOperationAsync(Guid operationId, int expectedVersion,
		CancellationToken cancellationToken = default) =>
		SendAsync<AssistantOperationResponse>(() => _httpClient.PostAsJsonAsync($"{OperationsPath}/{operationId}/cancel",
			new AssistantOperationVersionRequest(expectedVersion), cancellationToken), cancellationToken);

	private static async Task<AssistantApiResult<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
		where T : class
	{
		try
		{
			using var response = await send();

			if (!response.IsSuccessStatusCode)
			{
				return await FailureAsync<T>(response, cancellationToken);
			}

			var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);

			return value is null
				? AssistantApiResult<T>.Failure("The LifeOS API returned an empty response.")
				: AssistantApiResult<T>.Success(value);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return AssistantApiResult<T>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	// The API's problem: its readable text (validation messages first, then detail, then title) and its stable code.
	// A body that is not a problem (a proxy's bare 502) becomes UnavailableMessage, never a raw status code.
	private static async Task<AssistantApiResult<T>> FailureAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		try
		{
			var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>(cancellationToken);
			var message = problem?.Errors is { Count: > 0 } errors
				? string.Join(" ", errors.Values.SelectMany(messages => messages))
				: !string.IsNullOrWhiteSpace(problem?.Detail) ? problem.Detail : problem?.Title;

			if (!string.IsNullOrWhiteSpace(message))
			{
				return AssistantApiResult<T>.Failure(message, problem!.Code, problem.CurrentVersion);
			}
		}
		catch (Exception exception) when (exception is JsonException or NotSupportedException)
		{
			// Not a JSON problem body.
		}

		return AssistantApiResult<T>.Failure(response.StatusCode == HttpStatusCode.BadRequest ? "The request was rejected." : UnavailableMessage);
	}

	private sealed record ProblemResponse(string? Title, string? Detail, string? Code, int? CurrentVersion, Dictionary<string, string[]>? Errors);
}
