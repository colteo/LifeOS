using LifeOS.Api.Authentication;
using LifeOS.Application.Notifications.Devices;
using LifeOS.Contracts.Devices;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Notifications;

// AUTO-001 §12: the app's push registration of its installation, for the signed-in user.
// Transport only; no token is ever returned or logged.
public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var devices = endpoints.MapGroup("/api/devices").RequireAuthorization();

        devices.MapPut("/{installationId}", RegisterAsync).WithName("RegisterDevice");
        devices.MapDelete("/{installationId}", UnregisterAsync).WithName("UnregisterDevice");

        return endpoints;
    }

    public static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> RegisterAsync(
        string installationId,
        RegisterDeviceRequest request,
        AuthenticatedUser user,
        RegisterDeviceHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            user.UserId,
            new RegisterDeviceCommand(installationId, request.Platform, request.PushToken, request.NotificationsPermitted),
            cancellationToken);

        return ToHttp(result);
    }

    public static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> UnregisterAsync(
        string installationId,
        AuthenticatedUser user,
        UnregisterDeviceHandler handler,
        CancellationToken cancellationToken) =>
        ToHttp(await handler.HandleAsync(user.UserId, installationId, cancellationToken));

    private static Results<NoContent, ValidationProblem, ProblemHttpResult> ToHttp(DeviceRegistrationResult result) => result.Outcome switch
    {
        DeviceRegistrationOutcome.Done => TypedResults.NoContent(),
        DeviceRegistrationOutcome.Invalid => TypedResults.ValidationProblem(result.Errors),
        _ => TypedResults.Problem(title: "Device registration not found.", statusCode: StatusCodes.Status404NotFound)
    };
}
