using LifeOS.Api.Authentication;
using LifeOS.Application.Gym.Training;
using LifeOS.Contracts.Gym.Training;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Gym;

// Choosing a workout to train: the user's programs with their workouts, read-only. A workout is
// started through the workout session endpoints.
public static class TrainingEndpoints
{
    public static IEndpointRouteBuilder MapTrainingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Owned by the authenticated user; the user id comes only from the access token.
        var training = endpoints.MapGroup("/api/gym/training")
            .RequireAuthorization();

        training.MapGet("/programs", GetProgramsAsync).WithName("GetTrainingPrograms");

        return endpoints;
    }

    public static async Task<Ok<IReadOnlyList<TrainingProgramResponse>>> GetProgramsAsync(
        AuthenticatedUser user,
        GetTrainingProgramsHandler handler,
        CancellationToken cancellationToken)
    {
        var programs = await handler.HandleAsync(user.UserId, cancellationToken);

        IReadOnlyList<TrainingProgramResponse> response = programs
            .Select(program => new TrainingProgramResponse(
                program.Id,
                program.Name,
                program.Workouts
                    .Select(workout => new TrainingWorkoutResponse(
                        workout.Id,
                        workout.Name,
                        workout.Position,
                        workout.ExerciseCount,
                        workout.PrescribedSetCount,
                        workout.CanStart))
                    .ToList()))
            .ToList();

        return TypedResults.Ok(response);
    }
}
