using System.Net;
using System.Net.Http.Json;
using LifeOS.Contracts.Reminders;

namespace LifeOS.App.Services.Reminders;

// REM-001: the Reminders API. Calls that depend on the user's "now" carry the device's current UTC
// offset. Failures carry the API's readable message (400 validation messages, 409 details); a missing
// reminder (404, also another user's) is NotFoundMessage. Authentication and the 401 refresh come from
// the authorized HttpClient pipeline. The app never computes the next occurrence itself.
public sealed class RemindersApiClient
{
	public const string RemindersPath = "api/reminders";

	public const string NotFoundMessage = "This reminder no longer exists.";

	private readonly HttpClient _httpClient;
	private readonly Func<int> _utcOffsetMinutes;

	public RemindersApiClient(HttpClient httpClient, Func<int> utcOffsetMinutes)
	{
		_httpClient = httpClient;
		_utcOffsetMinutes = utcOffsetMinutes;
	}

	// The device's current offset, e.g. 120 for UTC+02:00.
	public static int DeviceUtcOffsetMinutes() => (int)DateTimeOffset.Now.Offset.TotalMinutes;

	public Task<ApiResult<RemindersResponse>> GetAllAsync(CancellationToken cancellationToken = default) =>
		SendAsync<RemindersResponse>(() => _httpClient.GetAsync(RemindersPath, cancellationToken), cancellationToken);

	public Task<ApiResult<ReminderResponse>> GetAsync(Guid reminderId, CancellationToken cancellationToken = default) =>
		SendAsync<ReminderResponse>(() => _httpClient.GetAsync(ReminderPath(reminderId), cancellationToken), cancellationToken);

	public Task<ApiResult<ReminderResponse>> CreateAsync(CreateReminderRequest request, CancellationToken cancellationToken = default) =>
		SendAsync<ReminderResponse>(() => _httpClient.PostAsJsonAsync(RemindersPath, request, cancellationToken), cancellationToken);

	public Task<ApiResult<ReminderResponse>> UpdateAsync(Guid reminderId, UpdateReminderRequest request, CancellationToken cancellationToken = default) =>
		SendAsync<ReminderResponse>(() => _httpClient.PutAsJsonAsync(ReminderPath(reminderId), request, cancellationToken), cancellationToken);

	public Task<ApiResult<ReminderResponse>> PauseAsync(Guid reminderId, CancellationToken cancellationToken = default) =>
		SendAsync<ReminderResponse>(() => _httpClient.PostAsync($"{ReminderPath(reminderId)}/pause", null, cancellationToken), cancellationToken);

	public Task<ApiResult<ReminderResponse>> ResumeAsync(Guid reminderId, CancellationToken cancellationToken = default) =>
		SendAsync<ReminderResponse>(() => _httpClient.PostAsJsonAsync($"{ReminderPath(reminderId)}/resume", new ResumeReminderRequest(_utcOffsetMinutes()),
			cancellationToken), cancellationToken);

	// minutes: 15, 60 or 180.
	public Task<ApiResult<ReminderResponse>> SnoozeAsync(Guid reminderId, int minutes, CancellationToken cancellationToken = default) =>
		SendAsync<ReminderResponse>(() => _httpClient.PostAsJsonAsync($"{ReminderPath(reminderId)}/snooze", new SnoozeReminderRequest(minutes),
			cancellationToken), cancellationToken);

	// A hard delete. A reminder that is already gone (404) counts as deleted.
	public async Task<ApiResult<bool>> DeleteAsync(Guid reminderId, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.DeleteAsync(ReminderPath(reminderId), cancellationToken);

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

	public static string ReminderPath(Guid reminderId) => $"{RemindersPath}/{reminderId}";

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
