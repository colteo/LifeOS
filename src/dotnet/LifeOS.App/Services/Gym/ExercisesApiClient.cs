using System.Net;
using System.Net.Http.Json;
using LifeOS.Contracts.Gym.Exercises;

namespace LifeOS.App.Services.Gym;

public sealed class ExercisesApiClient
{
	private const string ExercisesPath = "api/gym/exercises";

	private readonly HttpClient _httpClient;

	public ExercisesApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	public async Task<ApiResult<IReadOnlyList<ExerciseResponse>>> GetExercisesAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.GetAsync(ExercisesPath, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<IReadOnlyList<ExerciseResponse>>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var exercises = await response.Content.ReadFromJsonAsync<List<ExerciseResponse>>(cancellationToken);

			return ApiResult<IReadOnlyList<ExerciseResponse>>.Success(exercises ?? []);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<IReadOnlyList<ExerciseResponse>>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	// A 409 means the user already has an exercise with this name (ignoring case): reuse it instead.
	public async Task<ApiResult<ExerciseResponse>> CreateExerciseAsync(string name, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.PostAsJsonAsync(ExercisesPath, new CreateExerciseRequest(name), cancellationToken);

			if (response.StatusCode == HttpStatusCode.Conflict)
			{
				return ApiResult<ExerciseResponse>.Failure("You already have an exercise with this name. Select it from the list.");
			}

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<ExerciseResponse>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var exercise = await response.Content.ReadFromJsonAsync<ExerciseResponse>(cancellationToken);

			return exercise is null
				? ApiResult<ExerciseResponse>.Failure("The LifeOS API returned an empty response.")
				: ApiResult<ExerciseResponse>.Success(exercise);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<ExerciseResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}
}
