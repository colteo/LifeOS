using System.Globalization;
using LifeOS.App.Services.Finance;

namespace LifeOS.UnitTests.Finance;

public class AnalyticsDisplayTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    [Theory]
    [InlineData("USD", "EUR", "USD")]   // the current choice is kept when the month has it
    [InlineData("GBP", "EUR", "EUR")]   // otherwise the default currency
    [InlineData(null, "CHF", "EUR")]    // otherwise the first one
    public void PickCurrency_PrefersCurrentThenDefaultThenFirst(string? current, string? defaultCurrency, string expected)
    {
        Assert.Equal(expected, AnalyticsDisplay.PickCurrency(["EUR", "USD"], current, defaultCurrency));
    }

    [Fact]
    public void PickCurrency_WithNoActivity_IsNull()
    {
        Assert.Null(AnalyticsDisplay.PickCurrency([], "EUR", "EUR"));
    }

    [Fact]
    public void Amount_FollowsTheCultureAndNamesTheCurrency()
    {
        Assert.Equal("1,357.00 EUR", AnalyticsDisplay.Amount(1357m, "EUR", English));
        Assert.Equal("1.357,50 EUR", AnalyticsDisplay.Amount(1357.5m, "EUR", Italian));
        Assert.Equal("0.1234", AnalyticsDisplay.Number(0.1234m, English));
    }

    [Theory]
    [InlineData(143, "+143.00 EUR", "text-success")]
    [InlineData(-20.5, "−20.50 EUR", "text-danger")]
    [InlineData(0, "0.00 EUR", "")]
    public void NetFlow_IsSignedAndColoredBySign(decimal amount, string text, string cssClass)
    {
        Assert.Equal(text, AnalyticsDisplay.NetFlow(amount, "EUR", English));
        Assert.Equal(cssClass, AnalyticsDisplay.NetFlowCssClass(amount));
    }

    [Theory]
    [InlineData(395, 1357, "29.1%")]
    [InlineData(1357, 1357, "100%")]
    [InlineData(10, 0, "0%")]
    [InlineData(0, 100, "0%")]
    [InlineData(150, 100, "100%")]
    public void ShareWidth_IsTheShareOfTheTotal_ClampedAndCultureInvariant(decimal amount, decimal total, string width)
    {
        var previous = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = Italian; // CSS needs a dot, never a decimal comma

            Assert.Equal(width, AnalyticsDisplay.ShareWidth(amount, total));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
