using System.Threading.RateLimiting;
using LifeOS.Api.Authentication;
using LifeOS.Application.Notifications;
using LifeOS.Contracts.Notifications;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Notifications;

// AUTO-001 §12: POST /api/notifications/test. Transport only; the use case decides who receives what.
public static class NotificationEndpoints
{
    public const string TestPath = "/api/notifications/test";
    public const string TestRateLimitPolicy = "test-notification";

    // §12: 1 per minute and 10 per day per user, in memory (one instance). Only this endpoint is
    // limited: tick Phase A deliveries never pass through it.
    public static IServiceCollection AddTestNotificationRateLimit(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(TestRateLimitPolicy, context => RateLimitPartition.Get(
                context.User.FindFirst("sub")?.Value ?? string.Empty,
                _ => RateLimiter.CreateChained(
                    new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }),
                    new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromDays(1), QueueLimit = 0 }))));
        });

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(TestPath, SendTestAsync)
            .WithName("SendTestNotification")
            .RequireAuthorization()
            .RequireRateLimiting(TestRateLimitPolicy);

        return endpoints;
    }

    // No body and no query: the caller cannot choose text, devices, tokens or users.
    public static async Task<Results<Ok<TestNotificationResponse>, ProblemHttpResult>> SendTestAsync(
        HttpRequest request,
        AuthenticatedUser user,
        SendTestNotificationHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.QueryString.HasValue || request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            return TypedResults.Problem(
                title: "No body or query string expected.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await handler.HandleAsync(user.UserId, cancellationToken);

        return result.Outcome switch
        {
            TestNotificationOutcome.Sent => TypedResults.Ok(new TestNotificationResponse(result.Devices, result.Sent, result.Failed)),

            TestNotificationOutcome.NoActiveDevice => TypedResults.Problem(
                title: "No active device.",
                detail: "Allow notifications in the LifeOS app on this device first.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = "no_active_device" }),

            _ => TypedResults.Problem(
                title: "Push notifications are not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?> { ["code"] = "push_disabled" })
        };
    }
}
