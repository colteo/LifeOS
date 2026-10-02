using System.Net.Http.Json;
using LifeOS.Contracts.Gym.Programs;

namespace LifeOS.App.Services.Gym;

// Workout program authoring. Every edit inside a program returns the whole updated program, so pages
// render the stored state. Failures carry the API's readable message (e.g. a 404 "This workout does
// not exist." when the page is stale, or a 400 validation message).
public sealed class WorkoutProgramsApiClient
{
	private const string ProgramsPath = "api/gym/programs";

	private readonly HttpClient _httpClient;

	public WorkoutProgramsApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	public Task<ApiResult<List<WorkoutProgramSummaryResponse>>> GetProgramsAsync(CancellationToken cancellationToken = default) =>
		SendAsync<List<WorkoutProgramSummaryResponse>>(token => _httpClient.GetAsync(ProgramsPath, token), cancellationToken);

	public Task<ApiResult<WorkoutProgramResponse>> GetProgramAsync(Guid programId, CancellationToken cancellationToken = default) =>
		SendAsync<WorkoutProgramResponse>(token => _httpClient.GetAsync(ProgramPath(programId), token), cancellationToken);

	public Task<ApiResult<WorkoutProgramResponse>> CreateProgramAsync(string name, CancellationToken cancellationToken = default) =>
		SendAsync<WorkoutProgramResponse>(
			token => _httpClient.PostAsJsonAsync(ProgramsPath, new CreateWorkoutProgramRequest(name), token),
			cancellationToken);

	public Task<ApiResult<WorkoutProgramResponse>> RenameProgramAsync(Guid programId, string name, CancellationToken cancellationToken = default) =>
		SendAsync<WorkoutProgramResponse>(
			token => _httpClient.PutAsJsonAsync(ProgramPath(programId), new UpdateWorkoutProgramRequest(name), token),
			cancellationToken);

	// Deletes the program with its workouts and blocks; exercises are kept.
	public async Task<ApiResult<bool>> DeleteProgramAsync(Guid programId, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.DeleteAsync(ProgramPath(programId), cancellationToken);

			return response.IsSuccessStatusCode
				? ApiResult<bool>.Success(true)
				: ApiResult<bool>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<bool>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	public Task<ApiResult<WorkoutProgramResponse>> AddWorkoutAsync(Guid programId, string name, CancellationToken cancellationToken = default) =>
		SendAsync<WorkoutProgramResponse>(
			token => _httpClient.PostAsJsonAsync(WorkoutsPath(programId), new CreateWorkoutRequest(name), token),
			cancellationToken);

	public Task<ApiResult<WorkoutProgramResponse>> RenameWorkoutAsync(
		Guid programId,
		Guid workoutId,
		string name,
		CancellationToken cancellationToken = default) =>
		SendAsync<WorkoutProgramResponse>(
			token => _httpClient.PutAsJsonAsync($"{WorkoutsPath(programId)}/{workoutId}", new UpdateWorkoutRequest(name), token),
			cancellationToken);

	public Task<ApiResult<WorkoutProgramResponse>> DeleteWorkoutAsync(Guid programId, Guid workoutId, CancellationToken cancellationToken = default) =>
		SendAsync<WorkoutProgramResponse>(
			token => _httpClient.DeleteAsync($"{WorkoutsPath(programId)}/{workoutId}", token),
			cancellationToken);

	public Task<ApiResult<WorkoutProgramResponse>> ReorderWorkoutsAsync(
		Guid programId,
		IReadOnlyList<Guid> workoutIds,
		CancellationToken cancellationToken = default) =>
		SendAsync<WorkoutProgramResponse>(
			token => _httpClient.PutAsJsonAsync($"{WorkoutsPath(programId)}/order", new ReorderWorkoutsRequest(workoutIds), token),
			cancellationToken);

	public Task<ApiResult<WorkoutProgramResponse>> AddBlockAsync(
		Guid programId,
		Guid workoutId,
		CreateWorkoutBlockRequest request,
		CancellationToken cancellationToken = default) =>
		SendAsync<WorkoutProgramResponse>(
			token => _httpClient.PostAsJsonAsync(BlocksPath(programId, workoutId), request, token),
			cancellationToken);

	public Task<ApiResult<WorkoutProgramResponse>> UpdateBlockAsync(
		Guid programId,
		Guid workoutId,
		Guid blockId,
		UpdateWorkoutBlockRequest request,
		CancellationToken cancellationToken = default) =>
		SendAsync<WorkoutProgramResponse>(
			token => _httpClient.PutAsJsonAsync($"{BlocksPath(programId, workoutId)}/{blockId}", request, token),
			cancellationToken);

	public Task<ApiResult<WorkoutProgramResponse>> DeleteBlockAsync(
		Guid programId,
		Guid workoutId,
		Guid blockId,
		CancellationToken cancellationToken = default) =>
		SendAsync<WorkoutProgramResponse>(
			token => _httpClient.DeleteAsync($"{BlocksPath(programId, workoutId)}/{blockId}", token),
			cancellationToken);

	public Task<ApiResult<WorkoutProgramResponse>> ReorderBlocksAsync(
		Guid programId,
		Guid workoutId,
		IReadOnlyList<Guid> blockIds,
		CancellationToken cancellationToken = default) =>
		SendAsync<WorkoutProgramResponse>(
			token => _httpClient.PutAsJsonAsync($"{BlocksPath(programId, workoutId)}/order", new ReorderWorkoutBlocksRequest(blockIds), token),
			cancellationToken);

	private static string ProgramPath(Guid programId) => $"{ProgramsPath}/{programId}";

	private static string WorkoutsPath(Guid programId) => $"{ProgramPath(programId)}/workouts";

	private static string BlocksPath(Guid programId, Guid workoutId) => $"{WorkoutsPath(programId)}/{workoutId}/blocks";

	private static async Task<ApiResult<T>> SendAsync<T>(
		Func<CancellationToken, Task<HttpResponseMessage>> send,
		CancellationToken cancellationToken)
		where T : class
	{
		try
		{
			using var response = await send(cancellationToken);

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
