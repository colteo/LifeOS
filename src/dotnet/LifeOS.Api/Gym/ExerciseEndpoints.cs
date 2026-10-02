using LifeOS.Api.Authentication;
using LifeOS.Application.Gym.Exercises;
using LifeOS.Application.Gym.Exercises.CreateExercise;
using LifeOS.Application.Gym.Exercises.GetExercises;
using LifeOS.Contracts.Gym.Exercises;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Gym;

public static class ExerciseEndpoints
{
    public static IEndpointRouteBuilder MapExerciseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Exercises are owned by the authenticated user; the user id comes only from the access token.
        var exercises = endpoints.MapGroup("/api/gym/exercises")
            .RequireAuthorization();

        exercises.MapGet("/", GetExercisesAsync)
            .WithName("GetExercises");

        exercises.MapPost("/", CreateExerciseAsync)
            .WithName("CreateExercise");

        return endpoints;
    }

    public static async Task<Ok<IReadOnlyList<ExerciseResponse>>> GetExercisesAsync(
        AuthenticatedUser user,
        GetExercisesHandler handler,
        CancellationToken cancellationToken)
    {
        var exercises = await handler.HandleAsync(user.UserId, cancellationToken);

        IReadOnlyList<ExerciseResponse> response = exercises.Select(ToResponse).ToList();

        return TypedResults.Ok(response);
    }

    // 201 with the exercise, 409 when the user already has an exercise with this name (ignoring case).
    public static async Task<Results<Created<ExerciseResponse>, ValidationProblem, ProblemHttpResult>> CreateExerciseAsync(
        CreateExerciseRequest request,
        AuthenticatedUser user,
        CreateExerciseHandler handler,
        CancellationToken cancellationToken)
    {
        CreateExerciseResult result;

        try
        {
            result = await handler.HandleAsync(user.UserId, request.Name, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [exception.Message]
            });
        }

        if (result.Status == CreateExerciseStatus.DuplicateName)
        {
            return TypedResults.Problem(
                title: "Exercise already exists.",
                detail: "An exercise with this name already exists.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var response = ToResponse(result.Exercise!);

        return TypedResults.Created($"/api/gym/exercises/{response.Id}", response);
    }

    private static ExerciseResponse ToResponse(ExerciseSummary exercise) =>
        new(exercise.Id, exercise.Name, exercise.CreatedAtUtc);
}
