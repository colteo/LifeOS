using System.Net;
using System.Net.Http.Json;
using LifeOS.Contracts.Memory;

namespace LifeOS.App.Services.Journal;

// JRN-003: the Journal Memory API (AI-004/AI-006.1) used by Ask Journal: index status, bounded sync and
// ask. Failures carry the API's readable message (400 validation, 502/503 ProblemDetails); a bare
// unexpected status becomes UnavailableMessage, never a raw status code. Authentication and the 401
// refresh come from the authorized HttpClient pipeline; registered with ApiTimeouts.JournalMemory.
// Nothing here logs or stores a question, an answer or journal text.
public sealed class JournalMemoryApiClient
{
	public const string MemoryPath = "api/journal/memory";
	public const string StatusPath = MemoryPath + "/status";
	public const string SyncPath = MemoryPath + "/sync";
	public const string AskPath = MemoryPath + "/ask";

	public const string UnavailableMessage = "Ask Journal is unavailable right now. Your journal is safe; try again later.";

	private readonly HttpClient _httpClient;

	public JournalMemoryApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	public Task<ApiResult<JournalMemoryStatusResponse>> GetStatusAsync(CancellationToken cancellationToken = default) =>
		SendAsync<JournalMemoryStatusResponse>(() => _httpClient.GetAsync(StatusPath, cancellationToken), cancellationToken);

	// Indexes at most `limit` pending entries (the API allows 1–10).
	public Task<ApiResult<JournalMemorySyncResponse>> SyncAsync(int limit, CancellationToken cancellationToken = default) =>
		SendAsync<JournalMemorySyncResponse>(
			() => _httpClient.PostAsJsonAsync(SyncPath, new SyncJournalMemoryRequest(limit), cancellationToken), cancellationToken);

	public Task<ApiResult<JournalMemoryAnswerResponse>> AskAsync(string question, CancellationToken cancellationToken = default) =>
		SendAsync<JournalMemoryAnswerResponse>(
			() => _httpClient.PostAsJsonAsync(AskPath, new AskJournalMemoryRequest(question), cancellationToken), cancellationToken);

	private static async Task<ApiResult<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
		where T : class
	{
		try
		{
			using var response = await send();

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<T>.Failure(Readable(await ApiErrors.ReadAsync(response, cancellationToken), response.StatusCode));
			}

			var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);

			return value is null
				? ApiResult<T>.Failure("The LifeOS API returned an empty response.")
				: ApiResult<T>.Success(value);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<T>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	// ApiErrors' generic fallback names the status code (e.g. a proxy's bare 502): show a plain message instead.
	private static IReadOnlyList<string> Readable(IReadOnlyList<string> errors, HttpStatusCode status) =>
		errors is [var only] && only == $"The LifeOS API returned an unexpected response ({(int)status})."
			? [UnavailableMessage]
			: errors;
}
