using LifeOS.Api.Authentication;
using LifeOS.Api.Users;
using LifeOS.Application.Onboarding;
using LifeOS.Application.Onboarding.CompleteOnboarding;
using LifeOS.Application.Onboarding.SetUpFinanceProfile;
using LifeOS.Contracts.Onboarding;
using LifeOS.Contracts.Users;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Onboarding;

// First-run onboarding of the authenticated user. Each step returns the user's state after the
// step, in the same shape as GET /api/me. Both steps are safe to retry.
public static class OnboardingEndpoints
{
    public static IEndpointRouteBuilder MapOnboardingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var onboarding = endpoints.MapGroup("/api/onboarding")
            .RequireAuthorization();

        onboarding.MapPost("/finance-profile", SetUpFinanceProfileAsync)
            .WithName("SetUpFinanceProfile");

        onboarding.MapPost("/complete", CompleteOnboardingAsync)
            .WithName("CompleteOnboarding");

        return endpoints;
    }

    public static async Task<Results<Ok<MeResponse>, ValidationProblem, ProblemHttpResult>> SetUpFinanceProfileAsync(
        SetUpFinanceProfileRequest request,
        AuthenticatedUser user,
        SetUpFinanceProfileHandler handler,
        CancellationToken cancellationToken)
    {
        OnboardingResult result;

        try
        {
            result = await handler.HandleAsync(
                user.UserId,
                new SetUpFinanceProfileCommand(request.DefaultCurrency!),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return ValidationError("defaultCurrency", exception.Message);
        }

        return ToHttpResult(result);
    }

    public static async Task<Results<Ok<MeResponse>, ValidationProblem, ProblemHttpResult>> CompleteOnboardingAsync(
        AuthenticatedUser user,
        CompleteOnboardingHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId, cancellationToken);

        return ToHttpResult(result);
    }

    private static Results<Ok<MeResponse>, ValidationProblem, ProblemHttpResult> ToHttpResult(OnboardingResult result) =>
        result.Status switch
        {
            OnboardingResultStatus.Ok => TypedResults.Ok(MeEndpoints.ToResponse(result.User!)),

            OnboardingResultStatus.Invalid => ValidationError(result.Field!, result.Message!),

            OnboardingResultStatus.NotFound => TypedResults.Problem(
                title: "User not found.",
                detail: result.Message,
                statusCode: StatusCodes.Status404NotFound),

            _ => TypedResults.Problem(
                title: "Onboarding conflict.",
                detail: result.Message,
                statusCode: StatusCodes.Status409Conflict)
        };

    private static ValidationProblem ValidationError(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [message]
        });
}
