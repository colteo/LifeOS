using System.Globalization;
using LifeOS.App.Services.Finance;

namespace LifeOS.UnitTests.Finance;

public class PortfolioPrivacyTests
{
    [Fact]
    public void DefaultVisible_TogglesAndReloadsDevicePreference()
    {
        var stored = false;
        var privacy = new PortfolioPrivacy(() => stored, value => stored = value);
        var notifications = 0;
        privacy.Changed += () => notifications++;
        Assert.False(privacy.IsHidden);
        Assert.Equal("Hide portfolio amounts", privacy.ToggleLabel);
        Assert.Equal("1,234.50", privacy.Format(1234.5m, CultureInfo.GetCultureInfo("en-US")));
        privacy.Toggle();
        Assert.True(stored);
        Assert.Equal("Show portfolio amounts", privacy.ToggleLabel);
        Assert.Equal(PortfolioPrivacy.ObscuredAmount, privacy.Format(-1234.5m, CultureInfo.InvariantCulture));
        var reloaded = new PortfolioPrivacy(() => stored, value => stored = value);
        Assert.True(reloaded.IsHidden);
        reloaded.Toggle();
        Assert.False(stored);
        privacy.Toggle();
        Assert.False(privacy.IsHidden);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void SharedPreference_NotifiesBothConsumers_AndDoesNotChangeOtherFormatting()
    {
        var privacy = new PortfolioPrivacy(() => false, _ => { });
        var homeHidden = false;
        var portfolioHidden = false;
        privacy.Changed += () => homeHidden = privacy.IsHidden;
        privacy.Changed += () => portfolioHidden = privacy.IsHidden;
        var before = AnalyticsDisplay.Amount(12.50m, "EUR", CultureInfo.InvariantCulture);
        privacy.Toggle();
        Assert.True(homeHidden);
        Assert.True(portfolioHidden);
        Assert.Equal(before, AnalyticsDisplay.Amount(12.50m, "EUR", CultureInfo.InvariantCulture));
    }
}
