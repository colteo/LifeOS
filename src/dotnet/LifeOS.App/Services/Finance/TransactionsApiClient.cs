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
