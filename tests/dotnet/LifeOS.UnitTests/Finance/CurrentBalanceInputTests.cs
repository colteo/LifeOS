using LifeOS.App.Services.Finance;

namespace LifeOS.UnitTests.Finance;

public class CurrentBalanceInputTests
{
    private static readonly TimeZoneInfo UtcPlusTwo = TimeZoneInfo.CreateCustomTimeZone("Test/UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");

    // 18:05:42.123 local at UTC+2, i.e. 16:05:42.123Z.
    private static readonly DateTime LocalNow = new(2026, 9, 30, 18, 5, 42, 123);
    private static readonly DateTimeOffset UtcNow = new(2026, 9, 30, 16, 5, 42, 123, TimeSpan.Zero);

    [Fact]
    public void Disabled_BuildsNoOpeningBalance()
    {
        var input = new CurrentBalanceInput(LocalNow) { AmountText = "100" };

        Assert.True(input.TryBuildRequest(UtcNow, UtcPlusTwo, out var request, out var error));
        Assert.Null(request);
        Assert.Null(error);
    }

    [Fact]
    public void UnchangedDefaultTime_SendsTheExactCurrentInstant()
    {
        var input = Enabled("1250,00");

        Assert.True(input.TryBuildRequest(UtcNow, UtcPlusTwo, out var request, out _));
        Assert.Equal(1250m, request!.Amount);
        Assert.Equal(UtcNow, request.AsOfUtc); // not the start of the minute or of the day
    }

    [Fact]
    public void DefaultTime_IsTheCurrentLocalMinute()
    {
        Assert.Equal(new DateTime(2026, 9, 30, 18, 5, 0), new CurrentBalanceInput(LocalNow).AtLocal);
    }

    [Fact]
    public void ChangedTime_IsConvertedFromLocalToUtc()
    {
        var input = Enabled("-350,00");
        input.AtLocal = new DateTime(2026, 9, 29, 20, 0, 0);

        Assert.True(input.TryBuildRequest(UtcNow, UtcPlusTwo, out var request, out _));
        Assert.Equal(-350m, request!.Amount);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero), request.AsOfUtc);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("-200", -200)]
    [InlineData("75.50", 75.5)]
    public void ZeroAndNegativeAmounts_AreKept(string text, double expected)
    {
        Assert.True(Enabled(text).TryBuildRequest(UtcNow, UtcPlusTwo, out var request, out _));
        Assert.Equal((decimal)expected, request!.Amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.234,50")]
    [InlineData("abc")]
    public void MissingOrMalformedAmount_IsRejected(string text)
    {
        Assert.False(Enabled(text).TryBuildRequest(UtcNow, UtcPlusTwo, out var request, out var error));
        Assert.Null(request);
        Assert.NotNull(error);
    }

    [Fact]
    public void FutureTime_IsRejected()
    {
        var input = Enabled("10");
        input.AtLocal = LocalNow.AddHours(1);

        Assert.False(input.TryBuildRequest(UtcNow, UtcPlusTwo, out var request, out var error));
        Assert.Null(request);
        Assert.Contains("futuro", error);
    }

    [Fact]
    public void NonexistentLocalTime_IsRejectedWithTheConverterMessage()
    {
        var rome = TimeZoneInfo.CreateCustomTimeZone(
            "Test/Rome",
            TimeSpan.FromHours(1),
            "Test Rome",
            "CET",
            "CEST",
            [
                TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    DateTime.MinValue.Date,
                    DateTime.MaxValue.Date,
                    TimeSpan.FromHours(1),
                    TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                    TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday))
            ]);
        var input = Enabled("10");
        input.AtLocal = new DateTime(2026, 3, 29, 2, 30, 0);

        Assert.False(input.TryBuildRequest(UtcNow, rome, out _, out var error));
        Assert.Contains("ora legale", error);
    }

    [Fact]
    public void Reset_ClearsEverythingAndTakesTheNewNowAsDefault()
    {
        var input = Enabled("100");
        input.AtLocal = LocalNow.AddDays(-3);
        var later = LocalNow.AddMinutes(10);

        input.Reset(later);

        Assert.False(input.Enabled);
        Assert.Equal(string.Empty, input.AmountText);
        Assert.Equal(new DateTime(2026, 9, 30, 18, 15, 0), input.AtLocal);

        // The new default again means "exactly now".
        input.Enabled = true;
        input.AmountText = "1";
        var laterUtc = UtcNow.AddMinutes(10);
        Assert.True(input.TryBuildRequest(laterUtc, UtcPlusTwo, out var request, out _));
        Assert.Equal(laterUtc, request!.AsOfUtc);
    }

    private static CurrentBalanceInput Enabled(string amount) =>
        new(LocalNow) { Enabled = true, AmountText = amount };
}
