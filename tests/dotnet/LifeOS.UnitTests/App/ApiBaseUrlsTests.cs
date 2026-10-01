using LifeOS.App.Services;

namespace LifeOS.UnitTests.App;

public class ApiBaseUrlsTests
{
    [Fact]
    public void Development_PhysicalDevice_UsesLocalhostForBoth()
    {
        var (baseAddress, browserBaseAddress) = ApiBaseUrls.Development(isAndroidEmulator: false);

        Assert.Equal(new Uri("http://localhost:5050/"), baseAddress);
        Assert.Equal(new Uri("http://localhost:5050/"), browserBaseAddress);
    }

    [Fact]
    public void Development_Emulator_CallsTheHostButSignsInThroughLocalhost()
    {
        var (baseAddress, browserBaseAddress) = ApiBaseUrls.Development(isAndroidEmulator: true);

        Assert.Equal(new Uri("http://10.0.2.2:5050/"), baseAddress);
        Assert.Equal(new Uri("http://localhost:5050/"), browserBaseAddress);
    }

    [Theory]
    [InlineData("https://lifeos-api-123.europe-west1.run.app", "https://lifeos-api-123.europe-west1.run.app/")]
    [InlineData("https://lifeos-api-123.europe-west1.run.app/", "https://lifeos-api-123.europe-west1.run.app/")]
    [InlineData("  https://api.test.invalid:8443/lifeos  ", "https://api.test.invalid:8443/lifeos/")]
    public void Production_AcceptsAnHttpsUrl_AndEndsItWithASlash(string value, string expected)
    {
        Assert.True(ApiBaseUrls.TryParseProduction(value, out var baseAddress, out var error));

        Assert.Null(error);
        Assert.Equal(new Uri(expected), baseAddress);
        Assert.Equal(new Uri(baseAddress!, "api/me"), new Uri(expected + "api/me"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("api.test.invalid")]
    [InlineData("/api")]
    [InlineData("http://api.test.invalid")]
    [InlineData("ftp://api.test.invalid")]
    [InlineData("https://localhost:5050")]
    [InlineData("https://LOCALHOST")]
    [InlineData("https://dev.localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://127.1.2.3")]
    [InlineData("https://[::1]")]
    [InlineData("https://10.0.2.2:5050")]
    [InlineData("https://0.0.0.0")]
    [InlineData("https://user:secret@api.test.invalid")]
    [InlineData("https://api.test.invalid/?env=dev")]
    [InlineData("https://api.test.invalid/#x")]
    public void Production_RejectsMissingMalformedInsecureOrLocalUrls(string? value)
    {
        Assert.False(ApiBaseUrls.TryParseProduction(value, out var baseAddress, out var error));

        Assert.Null(baseAddress);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
