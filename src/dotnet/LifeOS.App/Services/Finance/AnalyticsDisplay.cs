using System.Globalization;

namespace LifeOS.App.Services.Finance;

// Display rules for the Analytics page. Amounts follow the device culture; every amount names its
// currency and currencies are never mixed. Plain .NET, no MAUI.
public static class AnalyticsDisplay
{
    // Expenses booked on a parent category itself, shown last in its breakdown.
    public const string DirectLabel = "Direct";

    // The currency to show: the current choice when the month has it, otherwise the user's default
    // currency, otherwise the first one. Null when the month has no activity at all.
    public static string? PickCurrency(IReadOnlyList<string> available, string? current, string? defaultCurrency)
    {
        if (current is not null && available.Contains(current))
        {
            return current;
        }

        if (defaultCurrency is not null && available.Contains(defaultCurrency))
        {
            return defaultCurrency;
        }

        return available.Count > 0 ? available[0] : null;
    }

    // "1,357.00 EUR": a positive quantity, culture-aware, 2 to 4 decimals.
    public static string Amount(decimal amount, string currency, CultureInfo? culture = null) =>
        $"{Number(amount, culture)} {currency}";

    public static string Number(decimal amount, CultureInfo? culture = null) =>
        amount.ToString("#,##0.00##", culture ?? CultureInfo.CurrentCulture);

    // "+143.00 EUR", "−20.00 EUR" or "0.00 EUR".
    public static string NetFlow(decimal amount, string currency, CultureInfo? culture = null) => amount switch
    {
        > 0 => $"+{Amount(amount, currency, culture)}",
        < 0 => $"−{Amount(-amount, currency, culture)}",
        _ => Amount(0m, currency, culture)
    };

    // Green when money came in net, red when it went out net, neutral at zero.
    public static string NetFlowCssClass(decimal amount) => amount switch
    {
        > 0 => "text-success",
        < 0 => "text-danger",
        _ => string.Empty
    };

    // CSS width of a share bar ("37.5%"): the amount's share of the total, clamped to 0-100%.
    public static string ShareWidth(decimal amount, decimal total)
    {
        if (total <= 0m || amount <= 0m)
        {
            return "0%";
        }

        var percent = Math.Min(100m, Math.Round(amount / total * 100m, 1));

        return $"{percent.ToString("0.#", CultureInfo.InvariantCulture)}%";
    }
}
