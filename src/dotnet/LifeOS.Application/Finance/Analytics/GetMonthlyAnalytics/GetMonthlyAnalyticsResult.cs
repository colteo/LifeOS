namespace LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics;

public enum GetMonthlyAnalyticsStatus
{
    Ok,
    Invalid
}

public sealed record GetMonthlyAnalyticsResult(
    GetMonthlyAnalyticsStatus Status,
    IReadOnlyList<CurrencyAnalytics> Currencies,
    string? Field,
    string? Message)
{
    public static GetMonthlyAnalyticsResult Ok(IReadOnlyList<CurrencyAnalytics> currencies) =>
        new(GetMonthlyAnalyticsStatus.Ok, currencies, null, null);

    public static GetMonthlyAnalyticsResult Invalid(string field, string message) =>
        new(GetMonthlyAnalyticsStatus.Invalid, [], field, message);
}
