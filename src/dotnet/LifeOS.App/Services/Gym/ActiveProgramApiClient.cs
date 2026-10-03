using System.Net;
using System.Net.Http.Json;
using LifeOS.Contracts.Gym.Training;

namespace LifeOS.App.Services.Gym;

// The program being trained (GYM-004): read it for Train, activate one from Programs, stop it.
// Failures carry the API's readable message (e.g. 409 "Another program is active...").
public sealed class ActiveProgramApiClient
{
	private const string ActiveProgramPath = "api/gym/active-program";

	private readonly HttpClient _httpClient;

	public ActiveProgramApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	// Success with null when no program is active.
	public async Task<ApiResult<ActiveProgramResponse?>> GetAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.GetAsync(ActiveProgramPath, cancellationToken);

			if (response.StatusCode == HttpStatusCode.NoContent)
			{
				return ApiResult<ActiveProgramResponse?>.Success(null);
			}

			var result = await ReadAsync(response, cancellationToken);

			return result.IsSuccess
				? ApiResult<ActiveProgramResponse?>.Success(result.Value)
				: ApiResult<ActiveProgramResponse?>.Failure(result.Errors);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<ActiveProgramResponse?>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	public async Task<ApiResult<ActiveProgramResponse>> ActivateAsync(Guid programId, int cycles, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.PostAsJsonAsync(ActiveProgramPath, new ActivateProgramRequest(programId, cycles), cancellationToken);

			return await ReadAsync(response, cancellationToken);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<ActiveProgramResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	// Ends the active program early; its finished workouts stay in History.
	public async Task<ApiResult<bool>> StopAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.PostAsync($"{ActiveProgramPath}/stop", null, cancellationToken);

			return response.IsSuccessStatusCode
				? ApiResult<bool>.Success(true)
				: ApiResult<bool>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<bool>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	private static async Task<ApiResult<ActiveProgramResponse>> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (!response.IsSuccessStatusCode)
		{
			return ApiResult<ActiveProgramResponse>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
		}

		var value = await response.Content.ReadFromJsonAsync<ActiveProgramResponse>(cancellationToken);

		return value is null
			? ApiResult<ActiveProgramResponse>.Failure("The LifeOS API returned an empty response.")
			: ApiResult<ActiveProgramResponse>.Success(value);
	}
}
