using LifeOS.Application.Automation;
using LifeOS.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Automation;

// AUTO-001 §7 and §13: POST /api/internal/automation/tick. Transport and authentication only; what is
// due is decided by RunAutomationTick. Mapped only when automation is enabled (AutomationConfiguration).
public static class AutomationTickEndpoints
{
    public const string TickPath = "/api/internal/automation/tick";
    public const string PolicyName = "AutomationTick";

    // Registers the tick and its dedicated key scheme + policy. AUTO-001 registers NO business
    // IAutomationHandler: modules add theirs here when they ship (AUTO-002+).
    public static IServiceCollection AddLifeOSAutomation(this IServiceCollection services, AutomationOptions options)
    {
        services.AddSingleton<AutomationTickGuard>();
        services.AddScoped<RunAutomationTick>();

        // Phase A uses the NotificationDispatcher registered by Program (enabled when FCM is configured).

        services.AddAuthentication()
            .AddScheme<AutomationKeyAuthenticationOptions, AutomationKeyAuthenticationHandler>(
                AutomationKeyAuthenticationHandler.SchemeName,
                scheme => scheme.KeyHash = AutomationKeyAuthenticationHandler.Hash(options.TickKey));

        services.AddAuthorization(authorization => authorization.AddPolicy(PolicyName, new AuthorizationPolicyBuilder()
            .AddAuthenticationSchemes(AutomationKeyAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireClaim(AutomationKeyAuthenticationHandler.ClaimType)
            .Build()));

        return services;
    }

    public static IEndpointRouteBuilder MapAutomationTickEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(TickPath, TickAsync)
            .WithName("RunAutomationTick")
            .RequireAuthorization(PolicyName)
            .ExcludeFromDescription();

        return endpoints;
    }

    // Parameterless by design: the scheduler cannot choose users, types or times. Not linked to
    // RequestAborted: a scheduler that gives up must not abort an item mid-way.
    public static async Task<Results<Ok<AutomationTickResponse>, Ok<AutomationTickSkippedResponse>, BadRequest>> TickAsync(
        HttpRequest request,
        RunAutomationTick tick,
        ILoggerFactory loggers)
    {
        if (request.QueryString.HasValue || request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            return TypedResults.BadRequest();
        }

        var result = await tick.RunAsync(CancellationToken.None);
        var logger = loggers.CreateLogger(typeof(AutomationTickEndpoints).FullName!);

        if (result.Skipped)
        {
            logger.LogInformation("Automation tick skipped: a tick started less than {Seconds} s ago on this instance.", (int)AutomationTickGuard.MinimumInterval.TotalSeconds);
            return TypedResults.Ok(new AutomationTickSkippedResponse(true));
        }

        logger.LogInformation(
            "Automation tick: {Deliveries} deliveries, {Executions} executions, more: {More}.", result.Deliveries, result.Executions, result.More);

        return TypedResults.Ok(new AutomationTickResponse(result.Deliveries, result.Executions, result.More));
    }

    // Counts only (the scheduler keeps response history), AUTO-001 §7.
    public sealed record AutomationTickResponse(int Deliveries, int Executions, bool More);

    public sealed record AutomationTickSkippedResponse(bool Skipped);
}
