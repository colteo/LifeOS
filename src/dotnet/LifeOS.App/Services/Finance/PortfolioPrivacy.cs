using System.Globalization;

namespace LifeOS.App.Services.Finance;

// One device-local state, shared by Home and Portfolio. Delegates allow testing without MAUI.
public sealed class PortfolioPrivacy
{
    public const string PreferenceKey = "finance.portfolio.amounts-hidden";
    public const string ObscuredAmount = "••••••";
    private readonly Action<bool> _save;

    public PortfolioPrivacy(Func<bool> load, Action<bool> save)
    {
        IsHidden = load();
        _save = save;
    }

    public bool IsHidden { get; private set; }
    public event Action? Changed;
    public string ToggleLabel => IsHidden ? "Show portfolio amounts" : "Hide portfolio amounts";

    public void Toggle()
    {
        var hidden = !IsHidden;
        _save(hidden);
        IsHidden = hidden;
        Changed?.Invoke();
    }

    public string Format(decimal amount, CultureInfo culture) =>
        IsHidden ? ObscuredAmount : amount.ToString("#,##0.00##", culture);
}
