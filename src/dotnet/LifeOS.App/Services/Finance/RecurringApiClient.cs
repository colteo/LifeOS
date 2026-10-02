using System.Net.Http.Json;
using LifeOS.Contracts.Finance.Recurring;

namespace LifeOS.App.Services.Finance;

public sealed class RecurringApiClient(HttpClient http)
{
    private static int Offset => (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes;
    public Task<ApiResult<RecurringResponse>> QueryAsync(DateTime from, DateTime to) =>
        SendAsync<RecurringResponse>(HttpMethod.Get, $"api/recurring?fromYear={from.Year}&fromMonth={from.Month}&toYear={to.Year}&toMonth={to.Month}&utcOffsetMinutes={Offset}", null);
    public Task<ApiResult<RecurringRuleResponse>> SaveAsync(Guid? id, SaveRecurringRuleRequest request) =>
        SendAsync<RecurringRuleResponse>(id is null ? HttpMethod.Post : HttpMethod.Put, "api/recurring" + (id is null ? "" : $"/{id}"), request);
    public Task<ApiResult<RecurringActionResponse>> DeleteAsync(Guid id) => SendAsync<RecurringActionResponse>(HttpMethod.Delete, $"api/recurring/{id}", null);
    public Task<ApiResult<RecurringActionResponse>> ActAsync(RecurringOccurrenceResponse o, string action, ConfirmRecurringRequest? request = null) =>
        SendAsync<RecurringActionResponse>(HttpMethod.Post, $"api/recurring/{o.RuleId}/{o.Year}/{o.Month}/{action}?utcOffsetMinutes={Offset}", request);

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
