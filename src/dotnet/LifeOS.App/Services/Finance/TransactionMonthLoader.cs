using LifeOS.Contracts.Finance.Recurring;
using LifeOS.Contracts.Finance.Transactions;

namespace LifeOS.App.Services.Finance;

public sealed record TransactionMonthData(ApiResult<IReadOnlyList<TransactionResponse>> History, ApiResult<RecurringResponse> Planning);

public static class TransactionMonthLoader
{
    public static async Task<TransactionMonthData> LoadAsync(DateTime selectedMonth, TimeZoneInfo zone,
        TransactionsApiClient transactions, RecurringApiClient recurring)
    {
        var month = LocalMonth.Containing(selectedMonth);
        var (fromUtc, toUtc) = LocalMonth.UtcRange(month, zone);
        var history = transactions.GetTransactionsAsync(fromUtc, toUtc);
        var planning = recurring.QueryAsync(month, month);
        await Task.WhenAll(history, planning);
        return new(history.Result, planning.Result);
    }
}
