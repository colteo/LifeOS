using System.Net;
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
        "/health/database",
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
        Assert.Superset(new HashSet<string> { "/api/auth/dev/sign-in", "/api/auth/refresh", "/api/auth/logout", "/health/database" }, anonymous);
    }

    [Theory]
    [InlineData("/api/accounts")]
    [InlineData("/api/categories")]
    [InlineData("/api/transactions")]
    [InlineData("/api/me")]
    [InlineData("/api/onboarding")]
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
    public async Task EndpointWithoutAuthorizationMetadata_IsProtectedByTheFallbackPolicy()
    {
        await using var factory = new LifeOSApiFactory();

        // The template endpoint declares nothing: the fallback policy must still protect it.
        var response = await factory.CreateClient().GetAsync("/weatherforecast");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static IEnumerable<RouteEndpoint> RouteEndpoints(LifeOSApiFactory factory) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    private static string Route(RouteEndpoint endpoint) => "/" + endpoint.RoutePattern.RawText!.TrimStart('/');
}
