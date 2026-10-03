namespace LifeOS.Api.Health;

// PROD-AI-001: public liveness for Render's health check and the external keepalive. Answers from the
// process alone: no database, no AI service, no version, environment, auth or configuration details.
public static class HealthEndpoints
{
    public const string LivePath = "/health/live";

    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(LivePath, () => TypedResults.Ok(new LiveResponse("ok")))
            .WithName("GetLiveness")
            .AllowAnonymous();

        return endpoints;
    }

    public sealed record LiveResponse(string Status);
}
