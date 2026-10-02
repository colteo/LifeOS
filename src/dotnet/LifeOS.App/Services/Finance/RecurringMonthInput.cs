using System.Globalization;

namespace LifeOS.App.Services.Finance;

public static class RecurringMonthInput
{
    public static bool TryParseOptional(string? text, out int? year, out int? month)
    {
        year = month = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateTime.TryParseExact(text, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || date.Year > 9998) return false;
        year = date.Year; month = date.Month;
        return true;
    }

    public static string EndLabel(int? year, int? month) => year is null || month is null
        ? "No end" : LocalMonth.Label(new DateTime(year.Value, month.Value, 1));
}
