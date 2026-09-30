using LifeOS.App.Services.Finance;

namespace LifeOS.UnitTests.Finance;

public class DecimalTextTests
{
    [Theory]
    [InlineData("12,50", "12.50")]
    [InlineData("12.50", "12.50")]
    [InlineData("12", "12")]
    [InlineData("0", "0")]
    [InlineData("  7,5  ", "7.5")]
    [InlineData("0,0001", "0.0001")]
    [InlineData("12,34567", "12.34567")] // decimal places are the API's concern
    public void AcceptsOneDecimalSeparatorEitherWay(string text, string expected)
    {
        Assert.True(DecimalText.TryParse(text, allowNegative: false, out var value));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("-350,00", "-350")]
    [InlineData("-350", "-350")]
    [InlineData("-0,50", "-0.5")]
    [InlineData(" -12.5 ", "-12.5")]
    public void AcceptsALeadingMinus_WhenAllowed(string text, string expected)
    {
        Assert.True(DecimalText.TryParse(text, allowNegative: true, out var value));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("-350")]
    [InlineData("-0,50")]
    public void RejectsASign_WhenNotAllowed(string text)
    {
        Assert.False(DecimalText.TryParse(text, allowNegative: false, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("--5")]
    [InlineData("- 5")]
    [InlineData("+5")]
    [InlineData("1.234,50")]
    [InlineData("1,234.50")]
    [InlineData("12,5,0")]
    [InlineData("1 234")]
    [InlineData("abc")]
    [InlineData("12€")]
    [InlineData("5-")]
    public void RejectsMalformedInput(string? text)
    {
        Assert.False(DecimalText.TryParse(text, allowNegative: true, out var value));
        Assert.Equal(0m, value);
    }
}
