using System.Globalization;
using System.Net.Http.Json;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.App.Services.Finance;

public sealed class TransactionsApiClient
{
	private const string TransactionsPath = "api/transactions";

	private readonly HttpClient _httpClient;

	public TransactionsApiClient(HttpClient httpClient)
	{
		_httpClient = httpClient;
	}

	// The newest transactions, newest first (the API allows 1-20).
	public async Task<ApiResult<IReadOnlyList<TransactionResponse>>> GetRecentTransactionsAsync(
		int limit = 5,
		CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.GetAsync(
				$"{TransactionsPath}/recent?limit={limit.ToString(CultureInfo.InvariantCulture)}",
				cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<IReadOnlyList<TransactionResponse>>.Failure(
					await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var transactions = await response.Content.ReadFromJsonAsync<List<TransactionResponse>>(cancellationToken);

			return ApiResult<IReadOnlyList<TransactionResponse>>.Success(transactions ?? []);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<IReadOnlyList<TransactionResponse>>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	// Half-open UTC range: fromUtc inclusive, toUtc exclusive.
	public async Task<ApiResult<IReadOnlyList<TransactionResponse>>> GetTransactionsAsync(
		DateTimeOffset fromUtc,
		DateTimeOffset toUtc,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var path = $"{TransactionsPath}?fromUtc={UtcQueryValue.Format(fromUtc)}&toUtc={UtcQueryValue.Format(toUtc)}";

			using var response = await _httpClient.GetAsync(path, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<IReadOnlyList<TransactionResponse>>.Failure(
					await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var transactions = await response.Content.ReadFromJsonAsync<List<TransactionResponse>>(cancellationToken);

			return ApiResult<IReadOnlyList<TransactionResponse>>.Success(transactions ?? []);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<IReadOnlyList<TransactionResponse>>.Failure(ApiErrors.UnreachableMessage);
		}
	}

	public async Task<ApiResult<TransactionResponse>> CreateTransactionAsync(
		CreateTransactionRequest request,
		CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await _httpClient.PostAsJsonAsync(TransactionsPath, request, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				return ApiResult<TransactionResponse>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
			}

			var transaction = await response.Content.ReadFromJsonAsync<TransactionResponse>(cancellationToken);

			return transaction is null
				? ApiResult<TransactionResponse>.Failure("The LifeOS API returned an empty response.")
				: ApiResult<TransactionResponse>.Success(transaction);
		}
		catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
		{
			return ApiResult<TransactionResponse>.Failure(ApiErrors.UnreachableMessage);
		}
	}
}
