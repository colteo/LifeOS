using LifeOS.Api.Authentication;
using LifeOS.Application.Users.GetCurrentUser;
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

        return TypedResults.Ok(new MeResponse(
            currentUser.UserId,
            currentUser.DisplayName,
            currentUser.Email,
            currentUser.OnboardingStatus.ToString(),
            currentUser.DefaultCurrency));
    }
}
