namespace LifeOS.App.Services.Finance;

// A local calendar month as the API sees it. The backend knows no time zone, so the app converts the
// device's local month boundaries to a half-open UTC range: [first day 00:00, next month's first day
// 00:00). Never computed by adding fixed hours or days in UTC, so daylight-saving changes inside the
// month are handled. Plain .NET, no MAUI.
public static class LocalMonth
{
    // The first day (00:00) of the local month containing the date.
    public static DateTime Containing(DateTime localDate) =>
        new(localDate.Year, localDate.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);

    public static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) UtcRange(DateTime month, TimeZoneInfo timeZone)
    {
        var first = Containing(month);

        return (WallClockToUtc(first, timeZone), WallClockToUtc(first.AddMonths(1), timeZone));
    }

    // "September 2026": English month names, whatever the device culture.
    public static string Label(DateTime month) =>
        Containing(month).ToString(
            TransactionDisplay.DateTextCulture.DateTimeFormat.YearMonthPattern,
            TransactionDisplay.DateTextCulture);

    // Treats the value explicitly as local wall-clock time. If it does not exist (a daylight-saving
    // gap at midnight in some zones), the first valid local minute after it is used.
    private static DateTimeOffset WallClockToUtc(DateTime wallClock, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);

        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
    }
}
