using LifeOS.Api.Authentication;
using LifeOS.Application.Gym.Sessions;
using LifeOS.Contracts.Gym.Sessions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Gym;

// Workout execution. A session is a snapshot of a workout taken at start; every change returns 200
// with the whole session so the client renders the stored state. 404 covers a missing session or set,
// including another user's; 409 a completed session (immutable) or, on start, a workout already in
// progress (its id is in the problem's "sessionId").
public static class WorkoutSessionEndpoints
{
    public static IEndpointRouteBuilder MapWorkoutSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Sessions are owned by the authenticated user; the user id comes only from the access token.
        var sessions = endpoints.MapGroup("/api/gym/sessions")
            .RequireAuthorization();

        sessions.MapPost("/", StartAsync).WithName("StartWorkoutSession");
        sessions.MapGet("/current", GetCurrentAsync).WithName("GetCurrentWorkoutSession");
        sessions.MapGet("/{sessionId:guid}", GetAsync).WithName("GetWorkoutSession");
        sessions.MapPut("/{sessionId:guid}/sets/{setId:guid}", RecordSetAsync).WithName("RecordWorkoutSet");
        sessions.MapPost("/{sessionId:guid}/finish", FinishAsync).WithName("FinishWorkoutSession");
        sessions.MapDelete("/{sessionId:guid}", DiscardAsync).WithName("DiscardWorkoutSession");

        return endpoints;
    }

    public static async Task<Results<Created<WorkoutSessionResponse>, ValidationProblem, ProblemHttpResult>> StartAsync(
        StartWorkoutSessionRequest request,
        AuthenticatedUser user,
        StartWorkoutSessionHandler handler,
        CancellationToken cancellationToken)
    {
        StartWorkoutSessionResult result;

        try
        {
            result = await handler.HandleAsync(user.UserId, request.ProgramId, request.WorkoutId, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return ValidationError(exception);
        }

        return result.Status switch
        {
            StartWorkoutSessionStatus.Started =>
                TypedResults.Created($"/api/gym/sessions/{result.Session!.Id}", ToResponse(result.Session)),
            StartWorkoutSessionStatus.AnotherInProgress => TypedResults.Problem(
                title: "Workout in progress.",
                detail: "Another workout is in progress. Finish or discard it first.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["sessionId"] = result.InProgressSessionId }),
            _ => TypedResults.Problem(
                title: "Workout not found.",
                detail: "This workout does not exist.",
                statusCode: StatusCodes.Status404NotFound)
        };
    }

    // 204 when no workout is in progress.
    public static async Task<Results<Ok<WorkoutSessionResponse>, NoContent>> GetCurrentAsync(
        AuthenticatedUser user,
        GetWorkoutSessionHandler handler,
        CancellationToken cancellationToken) =>
        await handler.HandleCurrentAsync(user.UserId, cancellationToken) is { } session
            ? TypedResults.Ok(ToResponse(session))
            : TypedResults.NoContent();

    public static async Task<Results<Ok<WorkoutSessionResponse>, ProblemHttpResult>> GetAsync(
        Guid sessionId,
        AuthenticatedUser user,
        GetWorkoutSessionHandler handler,
        CancellationToken cancellationToken) =>
        await handler.HandleAsync(user.UserId, sessionId, cancellationToken) is { } session
            ? TypedResults.Ok(ToResponse(session))
            : Failure(WorkoutSessionChangeStatus.SessionNotFound);

    public static async Task<Results<Ok<WorkoutSessionResponse>, ValidationProblem, ProblemHttpResult>> RecordSetAsync(
        Guid sessionId,
        Guid setId,
        RecordWorkoutSetRequest request,
        AuthenticatedUser user,
        RecordWorkoutSetHandler handler,
        CancellationToken cancellationToken)
    {
        WorkoutSessionChangeResult result;

        try
        {
            result = await handler.HandleAsync(user.UserId, sessionId, setId, request.ActualReps, request.WeightKg, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return ValidationError(exception);
        }

        return ToResult(result);
    }

    public static async Task<Results<Ok<WorkoutSessionResponse>, ValidationProblem, ProblemHttpResult>> FinishAsync(
        Guid sessionId,
        AuthenticatedUser user,
        FinishWorkoutSessionHandler handler,
        CancellationToken cancellationToken) =>
        ToResult(await handler.HandleAsync(user.UserId, sessionId, cancellationToken));

    // 204 when the in-progress workout and its snapshot are deleted.
    public static async Task<Results<NoContent, ProblemHttpResult>> DiscardAsync(
        Guid sessionId,
        AuthenticatedUser user,
        DiscardWorkoutSessionHandler handler,
        CancellationToken cancellationToken)
    {
        var status = await handler.HandleAsync(user.UserId, sessionId, cancellationToken);

        return status == WorkoutSessionChangeStatus.Changed ? TypedResults.NoContent() : Failure(status);
    }

    // ---- Mapping ----

    private static Results<Ok<WorkoutSessionResponse>, ValidationProblem, ProblemHttpResult> ToResult(WorkoutSessionChangeResult result) =>
        result.Status == WorkoutSessionChangeStatus.Changed
            ? TypedResults.Ok(ToResponse(result.Session!))
            : Failure(result.Status);

    private static ProblemHttpResult Failure(WorkoutSessionChangeStatus status) => status switch
    {
        WorkoutSessionChangeStatus.AlreadyCompleted => TypedResults.Problem(
            title: "Workout finished.",
            detail: "This workout is already finished and can no longer change.",
            statusCode: StatusCodes.Status409Conflict),
        WorkoutSessionChangeStatus.SetNotFound => TypedResults.Problem(
            title: "Set not found.",
            detail: "This set does not exist.",
            statusCode: StatusCodes.Status404NotFound),
        _ => TypedResults.Problem(
            title: "Workout session not found.",
            detail: "This workout session does not exist.",
            statusCode: StatusCodes.Status404NotFound)
    };

    private static WorkoutSessionResponse ToResponse(WorkoutSessionDetails session) =>
        new(
            session.Id,
            session.ProgramId,
            session.WorkoutId,
            session.ProgramName,
            session.WorkoutName,
            session.Status.ToString(),
            session.StartedAtUtc,
            session.CompletedAtUtc,
            session.CompletedSetCount,
            session.PrescribedSetCount,
            session.Rest is { } rest ? new WorkoutRestResponse(rest.StartedAtUtc, rest.EndsAtUtc, rest.Seconds) : null,
            session.ServerTimeUtc,
            session.Blocks
                .Select(block => new WorkoutSessionBlockResponse(
                    block.Id,
                    block.Position,
                    block.Kind.ToString(),
                    block.RestSeconds,
                    block.Exercises
                        .Select(exercise => new WorkoutSessionExerciseResponse(
                            exercise.Id,
                            exercise.Position,
                            exercise.ExerciseId,
                            exercise.ExerciseName,
                            exercise.Notes,
                            exercise.Sets
                                .Select(set => new WorkoutSessionSetResponse(
                                    set.Id,
                                    set.Position,
                                    set.TargetMinReps,
                                    set.TargetMaxReps,
                                    set.ActualReps,
                                    set.WeightKg,
                                    set.CompletedAtUtc))
                                .ToList()))
                        .ToList()))
                .ToList());

    private static ValidationProblem ValidationError(ArgumentException exception) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            [exception.ParamName ?? "request"] = [ValidationMessage(exception)]
        });

    // The rule only: Message also carries the parameter name and, when out of range, the actual value.
    private static string ValidationMessage(ArgumentException exception) =>
        exception.Message.Split('\n')[0].Replace($" (Parameter '{exception.ParamName}')", string.Empty).Trim();
}
