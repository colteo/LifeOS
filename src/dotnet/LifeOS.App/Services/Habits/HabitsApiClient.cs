using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using LifeOS.Contracts.Habits;

namespace LifeOS.App.Services.Habits;

// HAB-001: the Habit Tracker API. Every call that depends on the user's "today" carries the device's
// current UTC offset. Failures carry the API's readable message (400 validation messages, 409 details);
// a missing habit (404, also another user's) is NotFoundMessage. Authentication and the 401 refresh come
// from the authorized HttpClient pipeline. The app never computes statuses or statistics itself.
public sealed class HabitsApiClient
{
	public const string HabitsPath = "api/habits";

	public const string NotFoundMessage = "This habit no longer exists.";

	private readonly HttpClient _httpClient;
	private readonly Func<int> _utcOffsetMinutes;

	public HabitsApiClient(HttpClient httpClient, Func<int> utcOffsetMinutes)
	{
		_httpClient = httpClient;
		_utcOffsetMinutes = utcOffsetMinutes;
	}

	// The device's current offset, e.g. 120 for UTC+02:00.
	public static int DeviceUtcOffsetMinutes() => (int)DateTimeOffset.Now.Offset.TotalMinutes;

	public Task<ApiResult<HabitsForDayResponse>> GetDayAsync(DateOnly date, CancellationToken cancellationToken = default) =>
		SendAsync<HabitsForDayResponse>(() => _httpClient.GetAsync($"{HabitsPath}?date={Date(date)}&utcOffsetMinutes={Offset()}", cancellationToken), cancellationToken);

	public Task<ApiResult<HabitDetailResponse>> GetDetailAsync(Guid habitId, int windowDays, CancellationToken cancellationToken = default) =>
		SendAsync<HabitDetailResponse>(() => _httpClient.GetAsync($"{HabitPath(habitId)}?utcOffsetMinutes={Offset()}&window={windowDays}", cancellationToken),
			cancellationToken);

	public Task<ApiResult<HabitHistoryResponse>> GetHistoryAsync(Guid habitId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
		SendAsync<HabitHistoryResponse>(() => _httpClient.GetAsync(
			$"{HabitPath(habitId)}/history?from={Date(from)}&to={Date(to)}&utcOffsetMinutes={Offset()}", cancellationToken), cancellationToken);

	public Task<ApiResult<ArchivedHabitsResponse>> GetArchivedAsync(CancellationToken cancellationToken = default) =>
		SendAsync<ArchivedHabitsResponse>(() => _httpClient.GetAsync($"{HabitsPath}/archived", cancellationToken), cancellationToken);

	public Task<ApiResult<HabitDetailResponse>> CreateAsync(CreateHabitRequest request, CancellationToken cancellationToken = default) =>
		SendAsync<HabitDetailResponse>(() => _httpClient.PostAsJsonAsync(HabitsPath, request, cancellationToken), cancellationToken);

	public Task<ApiResult<HabitDetailResponse>> UpdateAsync(Guid habitId, UpdateHabitRequest request, CancellationToken cancellationToken = default) =>
		SendAsync<HabitDetailResponse>(() => _httpClient.PutAsJsonAsync(HabitPath(habitId), request, cancellationToken), cancellationToken);

	public Task<ApiResult<HabitDetailResponse>> ArchiveAsync(Guid habitId, CancellationToken cancellationToken = default) =>
		SendAsync<HabitDetailResponse>(() => _httpClient.PostAsJsonAsync($"{HabitPath(habitId)}/archive", new HabitTodayRequest(_utcOffsetMinutes()), cancellationToken),
			cancellationToken);

	public Task<ApiResult<HabitDetailResponse>> RestoreAsync(Guid habitId, CancellationToken cancellationToken = default) =>
		SendAsync<HabitDetailResponse>(() => _httpClient.PostAsJsonAsync($"{HabitPath(habitId)}/restore", new HabitTodayRequest(_utcOffsetMinutes()), cancellationToken),
			cancellationToken);

	// status: "Done" or "Excused". Idempotent on the server.
	public Task<ApiResult<HabitDayResponse>> SetCheckInAsync(Guid habitId, DateOnly date, string status, string? note,
		CancellationToken cancellationToken = default) =>
		SendAsync<HabitDayResponse>(() => _httpClient.PutAsJsonAsync(CheckInPath(habitId, date), new SetHabitCheckInRequest(status, note, _utcOffsetMinutes()),
			cancellationToken), cancellationToken);

	// Undo. Idempotent on the server.
	public Task<ApiResult<HabitDayResponse>> RemoveCheckInAsync(Guid habitId, DateOnly date, CancellationToken cancellationToken = default) =>
		SendAsync<HabitDayResponse>(() => _httpClient.DeleteAsync($"{CheckInPath(habitId, date)}?utcOffsetMinutes={Offset()}", cancellationToken),
			cancellationToken);

	// A hard delete with its history. A habit that is already gone (404) counts as deleted.
	public async Task<ApiResult<bool>> DeleteAsync(Guid habitId, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.DeleteAsync(HabitPath(habitId), cancellationToken);

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

	public static string HabitPath(Guid habitId) => $"{HabitsPath}/{habitId}";

	public static string CheckInPath(Guid habitId, DateOnly date) => $"{HabitPath(habitId)}/check-ins/{Date(date)}";

	private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

	private string Offset() => _utcOffsetMinutes().ToString(CultureInfo.InvariantCulture);

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
