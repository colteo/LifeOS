using System.Net;
using System.Net.Http.Json;
using LifeOS.Contracts.Gym.History;
using LifeOS.Contracts.Gym.Sessions;
using LifeOS.Contracts.Gym.Training;

namespace LifeOS.App.Services.Gym;

// Workout execution, the workouts to start it from, and the history of completed workouts. Every change returns the whole session, so
// pages render the stored state. Failures carry the API's readable message (e.g. 409 "This workout
// is already finished...").
public sealed class WorkoutSessionsApiClient
{
	private const string SessionsPath = "api/gym/sessions";
	private const string TrainingProgramsPath = "api/gym/training/programs";
	private const string HistoryPath = "api/gym/history";

	private readonly HttpClient _httpClient;

	public WorkoutSessionsApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	// The workouts to choose from on Train, grouped by program (read-only).
	public async Task<ApiResult<List<TrainingProgramResponse>>> GetTrainingProgramsAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.GetAsync(TrainingProgramsPath, cancellationToken);

			return await ReadAsync<List<TrainingProgramResponse>>(response, cancellationToken);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<List<TrainingProgramResponse>>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	// Started: Value is the new session. When another workout is in progress, the result fails and
	// InProgressSessionId names it so the page can offer to resume it.
	public async Task<StartWorkoutResult> StartAsync(Guid programId, Guid workoutId, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.PostAsJsonAsync(
				SessionsPath,
				new StartWorkoutSessionRequest(programId, workoutId),
				cancellationToken);

			if (response.StatusCode == HttpStatusCode.Conflict)
			{
				var conflict = await response.Content.ReadFromJsonAsync<InProgressProblem>(cancellationToken);

				return new StartWorkoutResult(ApiResult<WorkoutSessionResponse>.Failure(conflict?.Detail ?? "Another workout is in progress."), conflict?.SessionId);
			}

			return new StartWorkoutResult(await ReadAsync<WorkoutSessionResponse>(response, cancellationToken), null);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return new StartWorkoutResult(ApiResult<WorkoutSessionResponse>.Failure(ApiErrors.UnreachableMessage), null);
		}
	}

	// Success with null when no workout is in progress.
	public async Task<ApiResult<WorkoutSessionResponse?>> GetCurrentAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.GetAsync($"{SessionsPath}/current", cancellationToken);

			if (response.StatusCode == HttpStatusCode.NoContent)
			{
				return ApiResult<WorkoutSessionResponse?>.Success(null);
			}

			var result = await ReadAsync<WorkoutSessionResponse>(response, cancellationToken);

			return result.IsSuccess
				? ApiResult<WorkoutSessionResponse?>.Success(result.Value)
				: ApiResult<WorkoutSessionResponse?>.Failure(result.Errors);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<WorkoutSessionResponse?>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	public Task<ApiResult<WorkoutSessionResponse>> GetAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
		SendAsync(token => _httpClient.GetAsync(SessionPath(sessionId), token), cancellationToken);

	// Completes a set, or corrects it while the workout is in progress.
	public Task<ApiResult<WorkoutSessionResponse>> RecordSetAsync(
		Guid sessionId,
		Guid setId,
		int actualReps,
		decimal? weightKg,
		CancellationToken cancellationToken = default) =>
		SendAsync(
			token => _httpClient.PutAsJsonAsync($"{SessionPath(sessionId)}/sets/{setId}", new RecordWorkoutSetRequest(actualReps, weightKg), token),
			cancellationToken);

	public Task<ApiResult<WorkoutSessionResponse>> FinishAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
		SendAsync(token => _httpClient.PostAsync($"{SessionPath(sessionId)}/finish", null, token), cancellationToken);

	// Deletes the in-progress workout and everything recorded in it.
	public async Task<ApiResult<bool>> DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.DeleteAsync(SessionPath(sessionId), cancellationToken);

			return response.IsSuccessStatusCode
				? ApiResult<bool>.Success(true)
				: ApiResult<bool>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<bool>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	// One page of completed workouts, newest first; pass the previous page's NextCursor for the next.
	public async Task<ApiResult<WorkoutHistoryPageResponse>> GetHistoryAsync(string? cursor, CancellationToken cancellationToken = default)
	{
		try
		{
			var path = cursor is null ? HistoryPath : $"{HistoryPath}?cursor={Uri.EscapeDataString(cursor)}";
			using var response = await _httpClient.GetAsync(path, cancellationToken);

			return await ReadAsync<WorkoutHistoryPageResponse>(response, cancellationToken);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<WorkoutHistoryPageResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	// A completed workout, read-only.
	public Task<ApiResult<WorkoutSessionResponse>> GetHistoryDetailAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
		SendAsync(token => _httpClient.GetAsync($"{HistoryPath}/{sessionId}", token), cancellationToken);

	// What was recorded the last time each exercise of the session was done.
	public async Task<ApiResult<PreviousPerformanceResponse>> GetPreviousPerformanceAsync(Guid sessionId, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.GetAsync($"{SessionPath(sessionId)}/previous-performance", cancellationToken);

			return await ReadAsync<PreviousPerformanceResponse>(response, cancellationToken);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<PreviousPerformanceResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	private static string SessionPath(Guid sessionId) => $"{SessionsPath}/{sessionId}";

	private static async Task<ApiResult<WorkoutSessionResponse>> SendAsync(
		Func<CancellationToken, Task<HttpResponseMessage>> send,
		CancellationToken cancellationToken)
	{
		try
		{
			using var response = await send(cancellationToken);

			return await ReadAsync<WorkoutSessionResponse>(response, cancellationToken);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<WorkoutSessionResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
		where T : class
	{
		if (!response.IsSuccessStatusCode)
		{
			return ApiResult<T>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
		}

		var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);

		return value is null
			? ApiResult<T>.Failure("The LifeOS API returned an empty response.")
			: ApiResult<T>.Success(value);
	}

	private sealed record InProgressProblem(string? Detail, Guid? SessionId);
}

public sealed record StartWorkoutResult(ApiResult<WorkoutSessionResponse> Result, Guid? InProgressSessionId);
