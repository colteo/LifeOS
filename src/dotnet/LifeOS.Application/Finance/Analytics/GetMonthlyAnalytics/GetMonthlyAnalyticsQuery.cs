namespace LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics;

// Half-open range of UTC instants: FromUtc inclusive, ToUtc exclusive. The caller converts its local
// calendar month to UTC; the backend knows no time zone.
public sealed record GetMonthlyAnalyticsQuery(DateTimeOffset FromUtc, DateTimeOffset ToUtc);
