using System.Net.Http.Json;
using LifeOS.Contracts.Finance.Budgets;

namespace LifeOS.App.Services.Finance;

public sealed class BudgetsApiClient(HttpClient httpClient)
{
    private static string PathFor(DateTime month, string currency) =>
        $"api/budgets/{month.Year}/{month.Month}/{Uri.EscapeDataString(currency)}";

    public async Task<ApiResult<GetMonthlyBudgetResponse>> GetAsync(DateTime month, string currency, CancellationToken cancellationToken = default)
    {
        var (from, to) = LocalMonth.UtcRange(month, TimeZoneInfo.Local);
        var offset = (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes;
        try
        {
            using var response = await httpClient.GetAsync(
                $"{PathFor(month, currency)}?fromUtc={UtcQueryValue.Format(from)}&toUtc={UtcQueryValue.Format(to)}&utcOffsetMinutes={offset}", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return ApiResult<GetMonthlyBudgetResponse>.Failure(await ApiErrors.ReadAsync(response, cancellationToken));
            var value = await response.Content.ReadFromJsonAsync<GetMonthlyBudgetResponse>(cancellationToken);
            return value is null ? ApiResult<GetMonthlyBudgetResponse>.Failure("The LifeOS API returned an empty response.")
                : ApiResult<GetMonthlyBudgetResponse>.Success(value);
        }
        catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, cancellationToken))
        {
            return ApiResult<GetMonthlyBudgetResponse>.Failure(ApiErrors.UnreachableMessage);
        }
    }

    public Task<ApiResult<bool>> SetAsync(DateTime month, string currency, decimal amount) =>
        WriteAsync(month, currency, amount);
    public Task<ApiResult<bool>> DeleteAsync(DateTime month, string currency) =>
        WriteAsync(month, currency, null);

    private async Task<ApiResult<bool>> WriteAsync(DateTime month, string currency, decimal? amount)
    {
        try
        {
            using var response = amount is { } value
                ? await httpClient.PutAsJsonAsync(PathFor(month, currency), new SetMonthlyBudgetRequest(value))
                : await httpClient.DeleteAsync(PathFor(month, currency));
            return response.IsSuccessStatusCode ? ApiResult<bool>.Success(true)
                : ApiResult<bool>.Failure(await ApiErrors.ReadAsync(response, CancellationToken.None));
        }
        catch (Exception exception) when (ApiErrors.IsTransportFailure(exception, CancellationToken.None))
        {
            return ApiResult<bool>.Failure(ApiErrors.UnreachableMessage);
        }
    }
}
