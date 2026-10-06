using LifeOS.Api.Automation;
using Microsoft.Extensions.Configuration;

namespace LifeOS.UnitTests.Automation;

// AUTO-001 §13 and §6: Automation:TickKey enables automation; a weak key or missing tzdata fails startup.
public class AutomationConfigurationTests
{
    private const string ValidKey = "0123456789abcdefghijklmnopqrstuvwxyzABCD";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentKey_DisablesAutomation(string? key)
    {
        Assert.Null(AutomationConfiguration.Read(Configuration(key)));
    }

    [Fact]
    public void ValidKey_EnablesAutomation()
    {
        Assert.Equal(ValidKey, AutomationConfiguration.Read(Configuration($" {ValidKey} "))!.TickKey);
    }

    [Theory]
    [InlineData("short-key")]
    [InlineData("0123456789abcdefghijklmnopqrstu")] // 31 characters
    [InlineData("0123456789abcdefghij klmnopqrstuvwxyz")]
    [InlineData("0123456789abcdefghijklmnopqrstuvwxyzàèì")]
    public void MalformedKey_FailsStartup_WithoutRevealingIt(string key)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => AutomationConfiguration.Read(Configuration(key)));

        Assert.Contains(AutomationConfiguration.TickKeyKey, exception.Message);
        Assert.DoesNotContain(key.Trim(), exception.Message);
    }

    [Fact]
    public void MissingTimeZoneData_FailsStartup_WhenEnabled()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AutomationConfiguration.Read(Configuration(ValidKey), _ => throw new TimeZoneNotFoundException()));

        Assert.Contains("Europe/Rome", exception.Message);
    }

    [Fact]
    public void NonIanaReferenceZone_FailsStartup_WhenEnabled()
    {
        var custom = TimeZoneInfo.CreateCustomTimeZone("Custom", TimeSpan.FromHours(1), "Custom", "Custom");

        Assert.Throws<InvalidOperationException>(() => AutomationConfiguration.Read(Configuration(ValidKey), _ => custom));
    }

    [Fact]
    public void TimeZoneDataIsNotChecked_WhenDisabled()
    {
        Assert.Null(AutomationConfiguration.Read(Configuration(null), _ => throw new TimeZoneNotFoundException()));
    }

    [Fact]
    public void TheRealTimeZoneData_ResolvesTheReferenceZone()
    {
        AutomationConfiguration.EnsureTimeZoneData(TimeZoneInfo.FindSystemTimeZoneById);
    }

    private static IConfiguration Configuration(string? key) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AutomationConfiguration.TickKeyKey] = key })
            .Build();
}
