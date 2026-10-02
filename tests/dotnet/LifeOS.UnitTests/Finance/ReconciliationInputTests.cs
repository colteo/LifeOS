using LifeOS.App.Services.Finance;

namespace LifeOS.UnitTests.Finance;

public class ReconciliationInputTests
{
    [Theory]
    [InlineData("1250.00", 1237.5, 12.5)]
    [InlineData("1237,50", 1250, -12.5)]
    [InlineData("-350,25", -300, -50.25)]
    [InlineData("-300", -350, 50)]
    [InlineData("0", 0, 0)]
    public void SignedPreview_ParsesDecimalAndZero(string text, decimal previous, decimal difference)
    {
        var input = new ReconciliationInput { ActualBalance = text };
        Assert.True(input.TryPreview(previous, out var actual));
        Assert.Equal(difference, actual);
        Assert.True(input.TryBuildRequest(out var request, out var key));
        Assert.Equal(previous + difference, request!.ObservedBalance);
        Assert.NotEqual(Guid.Empty, key);
    }
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1,000.50")]
    public void InvalidInput_IsRejected(string text)
    {
        var input = new ReconciliationInput { ActualBalance = text };
        Assert.False(input.TryPreview(0, out _));
        Assert.False(input.TryBuildRequest(out _, out _));
    }
    [Fact]
    public void ExactRetry_KeepsKey_ChangedInputStartsNewSubmission()
    {
        var input = new ReconciliationInput { ActualBalance = "1250", Note = " bank " };
        input.TryBuildRequest(out var first, out var firstKey);
        input.ActualBalance = "1250,00";
        input.Note = "bank";
        input.TryBuildRequest(out var retry, out var retryKey);
        Assert.Equal(first, retry);
        Assert.Equal(firstKey, retryKey);
        input.ActualBalance = "1251";
        input.TryBuildRequest(out _, out var changedKey);
        Assert.NotEqual(firstKey, changedKey);
    }
    [Fact]
    public void UnavailableBalance_HasNoPreview()
    {
        Assert.False(new ReconciliationInput { ActualBalance = "1" }.TryPreview(null, out _));
    }
}
