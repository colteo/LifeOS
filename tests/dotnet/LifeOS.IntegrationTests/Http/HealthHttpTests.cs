using System.Net;
using System.Text.Json;
using LifeOS.Api.Nutrition;

namespace LifeOS.IntegrationTests.Http;

// PROD-AI-001: the public liveness probe used by Render's health check and the external keepalive.
// The factory's database host (unused.invalid) cannot be reached, so any database access would fail.
public class HealthHttpTests
{
    // Synthetic, test-only service key (never a real secret).
    private const string ServiceKey = "test-service-key-0123456789abcdefghijklmnop";

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Live_IsAnonymous_Minimal_AndNeverTouchesTheDatabase(string environment)
    {
        await using var factory = new LifeOSApiFactory(environment, developmentSignInEnabled: environment == "Development");

        var response = await factory.CreateClient().GetAsync("/health/live");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(["status"], json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Live_RevealsNoConfiguration_EvenWithTheAiServiceConfigured()
    {
        await using var factory = new LifeOSApiFactory("Production", developmentSignInEnabled: false, configure: builder => builder
            .UseSetting(NutritionAiConfiguration.BaseUrlKey, "https://lifeos-ai.example.test/")
            .UseSetting(NutritionAiConfiguration.ServiceKeyKey, ServiceKey));

        var body = await (await factory.CreateClient().GetAsync("/health/live")).Content.ReadAsStringAsync();

        Assert.Equal("""{"status":"ok"}""", body);
    }

    [Fact]
    public async Task UserEndpoints_StillRequireAuthentication()
    {
        await using var factory = new LifeOSApiFactory("Production", developmentSignInEnabled: false);

        var response = await factory.CreateClient().GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Production_WithAnAiServiceButNoServiceKey_RefusesToStart()
    {
        using var factory = new LifeOSApiFactory("Production", developmentSignInEnabled: false, configure: builder => builder
            .UseSetting(NutritionAiConfiguration.BaseUrlKey, "https://lifeos-ai.example.test/"));

        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains(NutritionAiConfiguration.ServiceKeyKey, failure.Message);
    }

    [Fact]
    public async Task Production_WithAnAiServiceAndAServiceKey_Starts()
    {
        await using var factory = new LifeOSApiFactory("Production", developmentSignInEnabled: false, configure: builder => builder
            .UseSetting(NutritionAiConfiguration.BaseUrlKey, "https://lifeos-ai.example.test/")
            .UseSetting(NutritionAiConfiguration.ServiceKeyKey, ServiceKey)
            .UseSetting(NutritionAiConfiguration.TimeoutSecondsKey, "120"));

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/health/live")).StatusCode);
    }
}
