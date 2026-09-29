using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Contracts.Finance.Accounts;

namespace LifeOS.App.Services.Finance;

public sealed class AccountsApiClient
{
	private const string AccountsPath = "api/accounts";
	private const string UnreachableMessage = "Could not reach the LifeOS API. Check that it is running and try again.";

	private readonly HttpClient _httpClient;

	public AccountsApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	public async Task<ApiResult<IReadOnlyList<AccountResponse>>> GetAccountsAsync(
		CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.GetAsync(AccountsPath, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<IReadOnlyList<AccountResponse>>.Failure(UnexpectedStatusMessage(response));
			}

			var accounts = await response.Content.ReadFromJsonAsync<List<AccountResponse>>(cancellationToken);

			return ApiResult<IReadOnlyList<AccountResponse>>.Success(accounts ?? []);
		}
		catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<IReadOnlyList<AccountResponse>>.Failure(UnreachableMessage);
		}
	}

	public async Task<ApiResult<AccountResponse>> CreateAccountAsync(
		CreateAccountRequest request,
		CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.PostAsJsonAsync(AccountsPath, request, cancellationToken);

			if (response.StatusCode == HttpStatusCode.BadRequest)
			{
				return ApiResult<AccountResponse>.Failure(
					await ReadValidationErrorsAsync(response, cancellationToken));
			}

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<AccountResponse>.Failure(UnexpectedStatusMessage(response));
			}

			var account = await response.Content.ReadFromJsonAsync<AccountResponse>(cancellationToken);

			return account is null
				? ApiResult<AccountResponse>.Failure("The LifeOS API returned an empty response.")
				: ApiResult<AccountResponse>.Success(account);
		}
		catch (Exception exception) when (IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<AccountResponse>.Failure(UnreachableMessage);
		}
	}

	private static async Task<IReadOnlyList<string>> ReadValidationErrorsAsync(
		HttpResponseMessage response,
		CancellationToken cancellationToken)
	{
		try
		{
			var problem = await response.Content.ReadFromJsonAsync<ValidationProblemResponse>(cancellationToken);

			if (problem?.Errors is { Count: > 0 } errors)
			{
				return errors.Values.SelectMany(messages => messages).ToList();
			}

			return [problem?.Title ?? "The request was rejected."];
		}
		catch (JsonException)
		{
			return ["The request was rejected."];
		}
	}

	private static string UnexpectedStatusMessage(HttpResponseMessage response) =>
		$"The LifeOS API returned an unexpected response ({(int)response.StatusCode}).";

	// Network errors, timeouts and unreadable responses are reported to the UI instead of thrown.
	// Cancellation requested by the caller still propagates.
	private static bool IsTransportFailure(Exception exception, CancellationToken cancellationToken) =>
		exception is HttpRequestException or JsonException
		|| (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested);

	private sealed record ValidationProblemResponse(string? Title, Dictionary<string, string[]>? Errors);
}
