using LifeOS.Api.Authentication;
using LifeOS.Application.Gym.Programs;
using LifeOS.Application.Gym.Programs.Blocks;
using LifeOS.Application.Gym.Programs.CreateWorkoutProgram;
using LifeOS.Application.Gym.Programs.DeleteWorkoutProgram;
using LifeOS.Application.Gym.Programs.GetWorkoutProgram;
using LifeOS.Application.Gym.Programs.GetWorkoutPrograms;
using LifeOS.Application.Gym.Programs.RenameWorkoutProgram;
using LifeOS.Application.Gym.Programs.Workouts;
using LifeOS.Contracts.Gym.Programs;
using LifeOS.Domain.Gym.Programs;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Gym;

// Workout program authoring. Every edit inside a program (workouts, blocks, order) returns 200 with
// the whole updated program, so the client renders the stored state. 404 covers a missing program,
// workout, block or exercise, including another user's; nothing is changed then.
public static class WorkoutProgramEndpoints
{
    private const string WorkoutsRoute = "/{programId:guid}/workouts";
    private const string BlocksRoute = WorkoutsRoute + "/{workoutId:guid}/blocks";

    public static IEndpointRouteBuilder MapWorkoutProgramEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Programs are owned by the authenticated user; the user id comes only from the access token.
        var programs = endpoints.MapGroup("/api/gym/programs")
            .RequireAuthorization();

        programs.MapGet("/", GetProgramsAsync).WithName("GetWorkoutPrograms");
        programs.MapPost("/", CreateProgramAsync).WithName("CreateWorkoutProgram");
        programs.MapGet("/{programId:guid}", GetProgramAsync).WithName("GetWorkoutProgram");
        programs.MapPut("/{programId:guid}", RenameProgramAsync).WithName("UpdateWorkoutProgram");
        programs.MapDelete("/{programId:guid}", DeleteProgramAsync).WithName("DeleteWorkoutProgram");

        programs.MapPost(WorkoutsRoute, AddWorkoutAsync).WithName("AddWorkout");
        programs.MapPut(WorkoutsRoute + "/order", ReorderWorkoutsAsync).WithName("ReorderWorkouts");
        programs.MapPut(WorkoutsRoute + "/{workoutId:guid}", RenameWorkoutAsync).WithName("UpdateWorkout");
        programs.MapDelete(WorkoutsRoute + "/{workoutId:guid}", DeleteWorkoutAsync).WithName("DeleteWorkout");

        programs.MapPost(BlocksRoute, AddBlockAsync).WithName("AddWorkoutBlock");
        programs.MapPut(BlocksRoute + "/order", ReorderBlocksAsync).WithName("ReorderWorkoutBlocks");
        programs.MapPut(BlocksRoute + "/{blockId:guid}", UpdateBlockAsync).WithName("UpdateWorkoutBlock");
        programs.MapDelete(BlocksRoute + "/{blockId:guid}", DeleteBlockAsync).WithName("DeleteWorkoutBlock");

        return endpoints;
    }

    // ---- Programs ----

    public static async Task<Ok<IReadOnlyList<WorkoutProgramSummaryResponse>>> GetProgramsAsync(
        AuthenticatedUser user,
        GetWorkoutProgramsHandler handler,
        CancellationToken cancellationToken)
    {
        var programs = await handler.HandleAsync(user.UserId, cancellationToken);

        IReadOnlyList<WorkoutProgramSummaryResponse> response = programs
            .Select(program => new WorkoutProgramSummaryResponse(program.Id, program.Name, program.WorkoutCount, program.CreatedAtUtc))
            .ToList();

        return TypedResults.Ok(response);
    }

    public static async Task<Results<Created<WorkoutProgramResponse>, ValidationProblem>> CreateProgramAsync(
        CreateWorkoutProgramRequest request,
        AuthenticatedUser user,
        CreateWorkoutProgramHandler handler,
        CancellationToken cancellationToken)
    {
        WorkoutProgramDetails program;

        try
        {
            program = await handler.HandleAsync(user.UserId, request.Name, cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return ValidationError(exception);
        }

        return TypedResults.Created($"/api/gym/programs/{program.Id}", ToResponse(program));
    }

    public static async Task<Results<Ok<WorkoutProgramResponse>, ProblemHttpResult>> GetProgramAsync(
        Guid programId,
        AuthenticatedUser user,
        GetWorkoutProgramHandler handler,
        CancellationToken cancellationToken)
    {
        var program = await handler.HandleAsync(user.UserId, programId, cancellationToken);

        return program is null
            ? NotFound(WorkoutProgramEditStatus.ProgramNotFound)
            : TypedResults.Ok(ToResponse(program));
    }

    public static Task<Results<Ok<WorkoutProgramResponse>, ValidationProblem, ProblemHttpResult>> RenameProgramAsync(
        Guid programId,
        UpdateWorkoutProgramRequest request,
        AuthenticatedUser user,
        RenameWorkoutProgramHandler handler,
        CancellationToken cancellationToken) =>
        EditAsync(() => handler.HandleAsync(user.UserId, programId, request.Name, cancellationToken));

    // 204 when deleted with its workouts, blocks and prescriptions; exercises are kept.
    public static async Task<Results<NoContent, ProblemHttpResult>> DeleteProgramAsync(
        Guid programId,
        AuthenticatedUser user,
        DeleteWorkoutProgramHandler handler,
        CancellationToken cancellationToken) =>
        await handler.HandleAsync(user.UserId, programId, cancellationToken)
            ? TypedResults.NoContent()
            : NotFound(WorkoutProgramEditStatus.ProgramNotFound);

    // ---- Workouts ----

    public static Task<Results<Ok<WorkoutProgramResponse>, ValidationProblem, ProblemHttpResult>> AddWorkoutAsync(
        Guid programId,
        CreateWorkoutRequest request,
        AuthenticatedUser user,
        AddWorkoutHandler handler,
        CancellationToken cancellationToken) =>
        EditAsync(() => handler.HandleAsync(user.UserId, programId, request.Name, cancellationToken));

    public static Task<Results<Ok<WorkoutProgramResponse>, ValidationProblem, ProblemHttpResult>> RenameWorkoutAsync(
        Guid programId,
        Guid workoutId,
        UpdateWorkoutRequest request,
        AuthenticatedUser user,
        RenameWorkoutHandler handler,
        CancellationToken cancellationToken) =>
        EditAsync(() => handler.HandleAsync(user.UserId, programId, workoutId, request.Name, cancellationToken));

    public static Task<Results<Ok<WorkoutProgramResponse>, ValidationProblem, ProblemHttpResult>> DeleteWorkoutAsync(
        Guid programId,
        Guid workoutId,
        AuthenticatedUser user,
        DeleteWorkoutHandler handler,
        CancellationToken cancellationToken) =>
        EditAsync(() => handler.HandleAsync(user.UserId, programId, workoutId, cancellationToken));

    public static Task<Results<Ok<WorkoutProgramResponse>, ValidationProblem, ProblemHttpResult>> ReorderWorkoutsAsync(
        Guid programId,
        ReorderWorkoutsRequest request,
        AuthenticatedUser user,
        ReorderWorkoutsHandler handler,
        CancellationToken cancellationToken) =>
        EditAsync(() => handler.HandleAsync(user.UserId, programId, request.WorkoutIds, cancellationToken));

    // ---- Blocks ----

    public static async Task<Results<Ok<WorkoutProgramResponse>, ValidationProblem, ProblemHttpResult>> AddBlockAsync(
        Guid programId,
        Guid workoutId,
        CreateWorkoutBlockRequest request,
        AuthenticatedUser user,
        AddWorkoutBlockHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseKind(request.Kind, out var kind))
        {
            return ValidationError("kind", "Block kind must be Single or Superset.");
        }

        return await EditAsync(() => handler.HandleAsync(
            user.UserId,
            new AddWorkoutBlockCommand(programId, workoutId, kind, request.RestSeconds, ToInputs(request.Exercises)),
            cancellationToken));
    }

    public static Task<Results<Ok<WorkoutProgramResponse>, ValidationProblem, ProblemHttpResult>> UpdateBlockAsync(
        Guid programId,
        Guid workoutId,
        Guid blockId,
        UpdateWorkoutBlockRequest request,
        AuthenticatedUser user,
        UpdateWorkoutBlockHandler handler,
        CancellationToken cancellationToken) =>
        EditAsync(() => handler.HandleAsync(
            user.UserId,
            new UpdateWorkoutBlockCommand(programId, workoutId, blockId, request.RestSeconds, ToInputs(request.Exercises)),
            cancellationToken));

    public static Task<Results<Ok<WorkoutProgramResponse>, ValidationProblem, ProblemHttpResult>> DeleteBlockAsync(
        Guid programId,
        Guid workoutId,
        Guid blockId,
        AuthenticatedUser user,
        DeleteWorkoutBlockHandler handler,
        CancellationToken cancellationToken) =>
        EditAsync(() => handler.HandleAsync(user.UserId, programId, workoutId, blockId, cancellationToken));

    public static Task<Results<Ok<WorkoutProgramResponse>, ValidationProblem, ProblemHttpResult>> ReorderBlocksAsync(
        Guid programId,
        Guid workoutId,
        ReorderWorkoutBlocksRequest request,
        AuthenticatedUser user,
        ReorderWorkoutBlocksHandler handler,
        CancellationToken cancellationToken) =>
        EditAsync(() => handler.HandleAsync(user.UserId, programId, workoutId, request.BlockIds, cancellationToken));

    // ---- Mapping ----

    private static async Task<Results<Ok<WorkoutProgramResponse>, ValidationProblem, ProblemHttpResult>> EditAsync(
        Func<Task<WorkoutProgramEditResult>> edit)
    {
        WorkoutProgramEditResult result;

        try
        {
            result = await edit();
        }
        catch (ArgumentException exception)
        {
            return ValidationError(exception);
        }

        return result.Status == WorkoutProgramEditStatus.Updated
            ? TypedResults.Ok(ToResponse(result.Program!))
            : NotFound(result.Status);
    }

    private static ProblemHttpResult NotFound(WorkoutProgramEditStatus status)
    {
        var (title, detail) = status switch
        {
            WorkoutProgramEditStatus.WorkoutNotFound => ("Workout not found.", "This workout does not exist."),
            WorkoutProgramEditStatus.BlockNotFound => ("Block not found.", "This block does not exist."),
            WorkoutProgramEditStatus.ExerciseNotFound => ("Exercise not found.", "An exercise of this block does not exist."),
            _ => ("Workout program not found.", "This workout program does not exist.")
        };

        return TypedResults.Problem(title: title, detail: detail, statusCode: StatusCodes.Status404NotFound);
    }

    private static IReadOnlyList<WorkoutBlockExerciseInput>? ToInputs(IReadOnlyList<WorkoutBlockExerciseRequest>? exercises) =>
        exercises?
            .Select(exercise => exercise is null
                ? null!
                : new WorkoutBlockExerciseInput(
                    exercise.ExerciseId,
                    exercise.Notes,
                    exercise.Sets?.Select(set => set is null ? null! : new SetTargetInput(set.TargetMinReps, set.TargetMaxReps)).ToList()))
            .ToList();

    private static WorkoutProgramResponse ToResponse(WorkoutProgramDetails program) =>
        new(
            program.Id,
            program.Name,
            program.CreatedAtUtc,
            program.Workouts
                .Select(workout => new WorkoutResponse(
                    workout.Id,
                    workout.Name,
                    workout.Position,
                    workout.Blocks
                        .Select(block => new WorkoutBlockResponse(
                            block.Id,
                            block.Position,
                            block.Kind.ToString(),
                            block.RestSeconds,
                            block.Exercises
                                .Select(exercise => new WorkoutBlockExerciseResponse(
                                    exercise.Id,
                                    exercise.Position,
                                    exercise.ExerciseId,
                                    exercise.ExerciseName,
                                    exercise.Notes,
                                    exercise.Sets
                                        .Select(set => new WorkoutSetResponse(set.Id, set.Position, set.TargetMinReps, set.TargetMaxReps))
                                        .ToList()))
                                .ToList()))
                        .ToList()))
                .ToList());

    private static bool TryParseKind(string? value, out WorkoutBlockKind kind)
    {
        // Match names only: Enum.TryParse would also accept numeric and comma-combined values.
        var name = Enum.GetNames<WorkoutBlockKind>()
            .FirstOrDefault(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase));

        kind = name is null ? default : Enum.Parse<WorkoutBlockKind>(name);

        return name is not null;
    }

    private static ValidationProblem ValidationError(ArgumentException exception) =>
        ValidationError(ToFieldName(exception.ParamName), exception.Message);

    private static string ToFieldName(string? parameterName) => parameterName switch
    {
        "targetMinReps" or "targetMaxReps" => "sets",
        null => "request",
        _ => parameterName
    };

    private static ValidationProblem ValidationError(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [message]
        });
}
