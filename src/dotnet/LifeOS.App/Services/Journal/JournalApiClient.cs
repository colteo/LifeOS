using System.Net;
using System.Net.Http.Json;
using LifeOS.Contracts.Journal;

namespace LifeOS.App.Services.Journal;

// JRN-002: the personal journal (JRN-001 API). Failures carry the API's readable message (400
// validation messages per field); a missing entry (404, also another user's) is NotFoundMessage.
// Authentication and the 401 refresh come from the authorized HttpClient pipeline.
public sealed class JournalApiClient
{
	public const string JournalPath = "api/journal";

	// The API's default page size (allowed: 1–50).
	public const int PageSize = 20;

	public const string NotFoundMessage = "This journal entry no longer exists.";

	private readonly HttpClient _httpClient;

	public JournalApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	// Newest first, exactly in the API's order; pass the previous page's NextCursor for the next page.
	public Task<ApiResult<JournalEntryPageResponse>> GetPageAsync(string? cursor, int limit = PageSize, CancellationToken cancellationToken = default) =>
		SendAsync<JournalEntryPageResponse>(() => _httpClient.GetAsync(PagePath(cursor, limit), cancellationToken), cancellationToken);

	public Task<ApiResult<JournalEntryResponse>> GetAsync(Guid entryId, CancellationToken cancellationToken = default) =>
		SendAsync<JournalEntryResponse>(() => _httpClient.GetAsync(EntryPath(entryId), cancellationToken), cancellationToken);

	public Task<ApiResult<JournalEntryResponse>> CreateAsync(CreateJournalEntryRequest request, CancellationToken cancellationToken = default) =>
		SendAsync<JournalEntryResponse>(() => _httpClient.PostAsJsonAsync(JournalPath, request, cancellationToken), cancellationToken);

	// Full replacement: a null Title removes the title.
	public Task<ApiResult<JournalEntryResponse>> UpdateAsync(Guid entryId, UpdateJournalEntryRequest request, CancellationToken cancellationToken = default) =>
		SendAsync<JournalEntryResponse>(() => _httpClient.PutAsJsonAsync(EntryPath(entryId), request, cancellationToken), cancellationToken);

	// A hard delete. An entry that is already gone (404) counts as deleted: the outcome the user asked for.
	public async Task<ApiResult<bool>> DeleteAsync(Guid entryId, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.DeleteAsync(EntryPath(entryId), cancellationToken);

			return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound
				? ApiResult<bool>.Success(true)
				: ApiResult<bool>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<bool>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	public static bool IsNotFound<T>(ApiResult<T> result) =>
		!result.IsSuccess && result.Errors is [NotFoundMessage];

	public static string PagePath(string? cursor, int limit) =>
		cursor is null
			? $"{JournalPath}?limit={limit}"
			: $"{JournalPath}?limit={limit}&cursor={Uri.EscapeDataString(cursor)}";

	public static string EntryPath(Guid entryId) => $"{JournalPath}/{entryId}";

	private static async Task<ApiResult<T>> SendAsync<T>(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
		where T : class
	{
		try
		{
			using var response = await send();

			if (response.StatusCode == HttpStatusCode.NotFound)
			{
				return ApiResult<T>.Failure(NotFoundMessage);
			}

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<T>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
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
}
