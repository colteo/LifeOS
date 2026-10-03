using System.Net.Http.Json;
using LifeOS.Contracts.Finance.PlannedExpenses;

namespace LifeOS.App.Services.Finance;

public sealed class PlannedExpensesApiClient(HttpClient http)
{
    private static int Offset => (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes;
    public Task<ApiResult<IReadOnlyList<PlannedExpenseResponse>>> QueryAsync(DateTime from, DateTime to) =>
        SendAsync<IReadOnlyList<PlannedExpenseResponse>>(HttpMethod.Get, FormattableString.Invariant($"api/planned-expenses?from={from:yyyy-MM-dd}&to={new DateTime(to.Year, to.Month, DateTime.DaysInMonth(to.Year, to.Month)):yyyy-MM-dd}&utcOffsetMinutes={Offset}"), null);
    public Task<ApiResult<PlannedExpenseSavedResponse>> SaveAsync(Guid? id, SavePlannedExpenseRequest request) =>
        SendAsync<PlannedExpenseSavedResponse>(id is null ? HttpMethod.Post : HttpMethod.Put, "api/planned-expenses" + (id is null ? "" : $"/{id}"), request);
    public Task<ApiResult<PlannedExpenseActionResponse>> DeleteAsync(Guid id) => SendAsync<PlannedExpenseActionResponse>(HttpMethod.Delete, $"api/planned-expenses/{id}", null);
    public Task<ApiResult<PlannedExpenseActionResponse>> ActAsync(PlannedExpenseResponse o, string action, ConfirmPlannedExpenseRequest? request = null) =>
        SendAsync<PlannedExpenseActionResponse>(HttpMethod.Post, $"api/planned-expenses/{o.Id}/{action}?utcOffsetMinutes={Offset}", request);

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body)
    {
        try
        {
            using var message = new HttpRequestMessage(method, path);
            if (body is not null) message.Content = JsonContent.Create(body, body.GetType());
            using var response = await http.SendAsync(message);
            if (!response.IsSuccessStatusCode) return ApiResult<T>.Failure(await ApiErrors.ReadAsync(response, default));
            var value = await response.Content.ReadFromJsonAsync<T>();
            return value is null ? ApiResult<T>.Failure("The LifeOS API returned an empty response.") : ApiResult<T>.Success(value);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
        { return ApiResult<T>.Failure("The LifeOS API returned an unreadable response."); }
        catch (Exception e) when (ApiErrors.IsTransportFailure(e, default)) { return ApiResult<T>.Failure(ApiErrors.UnreachableMessage); }
    }
}
