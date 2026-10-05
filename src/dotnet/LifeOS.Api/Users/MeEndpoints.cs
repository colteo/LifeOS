using LifeOS.Api.Authentication;
using LifeOS.Application.Users.GetCurrentUser;
using LifeOS.Application.Users.SetTimeZone;
using LifeOS.Contracts.Users;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Users;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/me", GetMeAsync)
            .WithName("GetMe")
            .RequireAuthorization();

        endpoints.MapPut("/api/me/time-zone", SetTimeZoneAsync)
            .WithName("SetMyTimeZone")
            .RequireAuthorization();

        return endpoints;
    }

    public static async Task<Results<Ok<MeResponse>, ProblemHttpResult>> GetMeAsync(
        AuthenticatedUser user,
        GetCurrentUserHandler handler,
        CancellationToken cancellationToken)
    {
        var currentUser = await handler.HandleAsync(user.UserId, cancellationToken);

        if (currentUser is null)
        {
            return TypedResults.Problem(
                title: "User not found.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return TypedResults.Ok(ToResponse(currentUser));
    }

    public static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetTimeZoneAsync(
        SetTimeZoneRequest request,
        AuthenticatedUser user,
        SetTimeZoneHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId, request.TimeZoneId, cancellationToken);

        return result switch
        {
            SetTimeZoneResult.Updated or SetTimeZoneResult.Unchanged => TypedResults.NoContent(),
            SetTimeZoneResult.Invalid => TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["timeZoneId"] = ["A valid IANA time zone id is required."]
            }),
            _ => TypedResults.Problem(title: "User not found.", statusCode: StatusCodes.Status404NotFound)
        };
    }

    // Shared with the onboarding endpoints, which return the user's state after each step.
    public static MeResponse ToResponse(CurrentUser currentUser) =>
        new(
            currentUser.UserId,
            currentUser.DisplayName,
            currentUser.Email,
            currentUser.OnboardingStatus.ToString(),
            currentUser.DefaultCurrency);
}
