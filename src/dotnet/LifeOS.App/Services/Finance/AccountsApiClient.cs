using System.Net;
using System.Net.Http.Json;
using LifeOS.Contracts.Finance.Accounts;

namespace LifeOS.App.Services.Finance;

public sealed class AccountsApiClient
{
	private const string AccountsPath = "api/accounts";
	private const string BalancesPath = "api/accounts/balances";

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
				return ApiResult<IReadOnlyList<AccountResponse>>.Failure(
					await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var accounts = await response.Content.ReadFromJsonAsync<List<AccountResponse>>(cancellationToken);

			return ApiResult<IReadOnlyList<AccountResponse>>.Success(accounts ?? []);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<IReadOnlyList<AccountResponse>>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	// Derived current balances (ADR-007), authoritative; the app never recomputes them.
	// "Current" is taken from this device's clock, the same clock that timestamps a "current balance"
	// entered here: with the server's default (its own clock), a device slightly ahead would read
	// just before a balance it had just declared and get "not available".
	public async Task<ApiResult<IReadOnlyList<AccountBalanceResponse>>> GetBalancesAsync(
		CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.GetAsync(
				$"{BalancesPath}?atUtc={UtcQueryValue.Format(DateTimeOffset.UtcNow)}",
				cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<IReadOnlyList<AccountBalanceResponse>>.Failure(
					await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var balances = await response.Content.ReadFromJsonAsync<List<AccountBalanceResponse>>(cancellationToken);

			return ApiResult<IReadOnlyList<AccountBalanceResponse>>.Success(balances ?? []);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<IReadOnlyList<AccountBalanceResponse>>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	public async Task<ApiResult<AccountResponse>> CreateAccountAsync(
		CreateAccountRequest request,
		CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.PostAsJsonAsync(AccountsPath, request, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<AccountResponse>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var account = await response.Content.ReadFromJsonAsync<AccountResponse>(cancellationToken);

			return account is null
				? ApiResult<AccountResponse>.Failure("The LifeOS API returned an empty response.")
				: ApiResult<AccountResponse>.Success(account);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<AccountResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	// Name and type only; the currency cannot be changed.
	public async Task<ApiResult<AccountResponse>> UpdateAccountAsync(
		Guid accountId,
		UpdateAccountRequest request,
		CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.PutAsJsonAsync($"{AccountsPath}/{accountId}", request, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<AccountResponse>.Failure(await ReadFailureAsync(response, cancellationToken));
			}

			var account = await response.Content.ReadFromJsonAsync<AccountResponse>(cancellationToken);

			return account is null
				? ApiResult<AccountResponse>.Failure("The LifeOS API returned an empty response.")
				: ApiResult<AccountResponse>.Success(account);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<AccountResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	// Deletes the account and its opening balance. A 409 explains why it is blocked (e.g. it has
	// transactions) with the API's message.
	public async Task<ApiResult<bool>> DeleteAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.DeleteAsync($"{AccountsPath}/{accountId}", cancellationToken);

			return response.IsSuccessStatusCode
				? ApiResult<bool>.Success(true)
				: ApiResult<bool>.Failure(await ReadFailureAsync(response, cancellationToken));
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<bool>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	// Adds the opening balance ("current balance") of an existing account that has none (ADR-007).
	public async Task<ApiResult<OpeningBalanceResponse>> SetOpeningBalanceAsync(
		Guid accountId,
		OpeningBalanceRequest request,
		CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.PutAsJsonAsync(
				$"{AccountsPath}/{accountId}/opening-balance",
				request,
				cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<OpeningBalanceResponse>.Failure(await ReadFailureAsync(response, cancellationToken));
			}

			var openingBalance = await response.Content.ReadFromJsonAsync<OpeningBalanceResponse>(cancellationToken);

			return openingBalance is null
				? ApiResult<OpeningBalanceResponse>.Failure("The LifeOS API returned an empty response.")
				: ApiResult<OpeningBalanceResponse>.Success(openingBalance);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<OpeningBalanceResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	// A 404 means the page is stale (deleted elsewhere); anything else uses the API's message.
	private static async Task<IReadOnlyList<string>> ReadFailureAsync(
		HttpResponseMessage response,
		CancellationToken cancellationToken) =>
		response.StatusCode == HttpStatusCode.NotFound
			? ["This account no longer exists. Reload the page."]
			: await ApiErrors.ReadAsync(response, cancellationToken);
}
