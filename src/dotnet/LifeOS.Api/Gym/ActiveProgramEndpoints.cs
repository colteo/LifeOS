using LifeOS.Api.Authentication;
using LifeOS.Application.Gym.Training;
using LifeOS.Contracts.Gym.Training;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Gym;

// The program being trained (GYM-004): one Active program per user, repeated for a number of cycles.
// Its workouts are started through the workout session endpoints; finishing one counts it for the
// current cycle. 404 covers a missing program, including another user's; 409 an already active one
// (its id is in the problem's "activeProgramId").
public static class ActiveProgramEndpoints
{
    public static IEndpointRouteBuilder MapActiveProgramEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Owned by the authenticated user; the user id comes only from the access token.
        var active = endpoints.MapGroup("/api/gym/active-program")
            .RequireAuthorization();

        active.MapGet("/", GetAsync).WithName("GetActiveProgram");
        active.MapPost("/", ActivateAsync).WithName("ActivateProgram");
        active.MapPost("/stop", StopAsync).WithName("StopActiveProgram");

        return endpoints;
    }

    // 204 when no program is active (none activated, or the last one completed or stopped).
    public static async Task<Results<Ok<ActiveProgramResponse>, NoContent>> GetAsync(
        AuthenticatedUser user,
        GetActiveProgramHandler handler,
        CancellationToken cancellationToken) =>
        await handler.HandleAsync(user.UserId, cancellationToken) is { } program
            ? TypedResults.Ok(ToResponse(program))
            : TypedResults.NoContent();

    public static async Task<Results<Created<ActiveProgramResponse>, ValidationProblem, ProblemHttpResult>> ActivateAsync(
        ActivateProgramRequest request,
        AuthenticatedUser user,
        ActivateProgramHandler handler,
        CancellationToken cancellationToken)
    {
        ActivateProgramResult result;

        try
        {
            result = await handler.HandleAsync(user.UserId, request.ProgramId, request.Cycles, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [exception.ParamName ?? "request"] = [ValidationMessage(exception)]
            });
        }

        return result.Status switch
        {
            ActivateProgramStatus.Activated =>
                TypedResults.Created("/api/gym/active-program", ToResponse(result.Program!)),
            ActivateProgramStatus.AnotherActive => TypedResults.Problem(
                title: "Program already active.",
                detail: "Another program is active. Stop it first.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["activeProgramId"] = result.ActiveProgramId }),
            _ => TypedResults.Problem(
                title: "Program not found.",
                detail: "This program does not exist.",
                statusCode: StatusCodes.Status404NotFound)
        };
    }

    // 204 when stopped; 404 when no program is active.
    public static async Task<Results<NoContent, ProblemHttpResult>> StopAsync(
        AuthenticatedUser user,
        StopActiveProgramHandler handler,
        CancellationToken cancellationToken) =>
        await handler.HandleAsync(user.UserId, cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.Problem(
                title: "No active program.",
                detail: "No program is active.",
                statusCode: StatusCodes.Status404NotFound);

    private static ActiveProgramResponse ToResponse(ActiveProgramDetails program) =>
        new(
            program.Id,
            program.ProgramId,
            program.ProgramName,
            program.TotalCycles,
            program.CurrentCycle,
            program.ActivatedAtUtc,
            program.ToDo
                .Select(workout => new TrainingWorkoutResponse(
                    workout.Id,
                    workout.Name,
                    workout.Position,
                    workout.ExerciseCount,
                    workout.PrescribedSetCount,
                    workout.CanStart))
                .ToList(),
            program.Done
                .Select(workout => new CompletedCycleWorkoutResponse(
                    workout.Id,
                    workout.Name,
                    workout.Position,
                    workout.SessionId,
                    workout.CompletedAtUtc))
                .ToList());

    // The rule only: Message also carries the parameter name and, when out of range, the actual value.
    private static string ValidationMessage(ArgumentException exception) =>
        exception.Message.Split('\n')[0].Replace($" (Parameter '{exception.ParamName}')", string.Empty).Trim();
}
