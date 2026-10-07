using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LifeOS.IntegrationTests.Http;

// ADR-006: every endpoint requires an authenticated user unless it is explicitly anonymous,
// and the anonymous set is a small, reviewed allowlist. A new endpoint cannot become
// anonymous by accident.
public class EndpointAuthorizationHttpTests
{
    private static readonly HashSet<string> AnonymousAllowlist =
    [
        "/api/auth/dev/sign-in",
        "/api/auth/refresh",
        "/api/auth/logout",
        "/api/auth/token",
        "/api/auth/google/start",
        "/api/auth/google/complete",
        "/health/live", // PROD-AI-001: process liveness only
        "/health/database", // Development only
        "/openapi/{documentName}.json"
    ];

    [Fact]
    public async Task FallbackPolicy_RequiresAnAuthenticatedUser()
    {
        await using var factory = new LifeOSApiFactory();
        factory.CreateClient();

        var fallback = factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy;

        Assert.NotNull(fallback);
        Assert.Contains(fallback.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task OnlyAllowlistedEndpoints_AreAnonymous()
    {
        await using var factory = new LifeOSApiFactory("Development", developmentSignInEnabled: true);
        factory.CreateClient();

        var anonymous = RouteEndpoints(factory)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(endpoint => Route(endpoint))
            .ToHashSet();

        Assert.Subset(AnonymousAllowlist, anonymous);
        // Guard against a vacuous pass: the known anonymous endpoints are actually discovered.
        Assert.Superset(new HashSet<string> { "/api/auth/dev/sign-in", "/api/auth/refresh", "/api/auth/logout", "/health/live", "/health/database" }, anonymous);
    }

    [Theory]
    [InlineData("/api/accounts")]
    [InlineData("/api/categories")]
    [InlineData("/api/transactions")]
    [InlineData("/api/gym")]
    [InlineData("/api/nutrition")]
    [InlineData("/api/me")]
    [InlineData("/api/onboarding")]
    [InlineData("/api/devices")]
    [InlineData("/api/journal")]
    public async Task UserDataEndpoints_ExplicitlyRequireAuthorization(string route)
    {
        await using var factory = new LifeOSApiFactory();
        factory.CreateClient();

        var endpoints = RouteEndpoints(factory)
            .Where(endpoint => Route(endpoint).StartsWith(route, StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, endpoint =>
        {
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.NotEmpty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        });
    }

    [Fact]
    public async Task OnlyAllowlistedEndpoints_AreAnonymous_InProduction()
    {
        await using var factory = new LifeOSApiFactory("Production", developmentSignInEnabled: false);
        factory.CreateClient();

        var anonymous = RouteEndpoints(factory)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(endpoint => Route(endpoint))
            .ToHashSet();

        Assert.Equal(
            new HashSet<string>
            {
                "/api/auth/refresh",
                "/api/auth/logout",
                "/api/auth/token",
                "/api/auth/google/start",
                "/api/auth/google/complete",
                "/health/live"
            },
            anonymous);
    }

    // Development-only and template endpoints are not mapped in Production at all. (Anonymous
    // requests to unmapped paths get 401 from the fallback policy, so these send a valid token.)
    [Theory]
    [InlineData("/weatherforecast")]
    [InlineData("/health/database")]
    [InlineData("/openapi/v1.json")]
    public async Task DevelopmentAndTemplateEndpoints_DoNotExistInProduction(string path)
    {
        await using var factory = new LifeOSApiFactory("Production", developmentSignInEnabled: false);

        var response = await GetAuthenticatedAsync(factory, path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WeatherForecastTemplate_DoesNotExistInDevelopment()
    {
        await using var factory = new LifeOSApiFactory();

        Assert.DoesNotContain(RouteEndpoints(factory), endpoint => Route(endpoint) == "/weatherforecast");
        var response = await GetAuthenticatedAsync(factory, "/weatherforecast");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_IsServedInDevelopment()
    {
        await using var factory = new LifeOSApiFactory();

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> GetAuthenticatedAsync(LifeOSApiFactory factory, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.IssueAccessToken(Guid.CreateVersion7()));

        return factory.CreateClient().SendAsync(request);
    }

    private static IEnumerable<RouteEndpoint> RouteEndpoints(LifeOSApiFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    private static string Route(RouteEndpoint endpoint) => "/" + endpoint.RoutePattern.RawText!.TrimStart('/');
}
