namespace LifeOS.Contracts.Gym.Programs;

public sealed record WorkoutProgramSummaryResponse(Guid Id, string Name, int WorkoutCount, DateTimeOffset CreatedAtUtc);

// The whole program; every list is ordered by position.
public sealed record WorkoutProgramResponse(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<WorkoutResponse> Workouts);

public sealed record WorkoutResponse(Guid Id, string Name, int Position, IReadOnlyList<WorkoutBlockResponse> Blocks);

public sealed record WorkoutBlockResponse(
    Guid Id,
    int Position,
    string Kind,
    int? RestSeconds,
    IReadOnlyList<WorkoutBlockExerciseResponse> Exercises);

public sealed record WorkoutBlockExerciseResponse(
    Guid Id,
    int Position,
    Guid ExerciseId,
    string ExerciseName,
    string? Notes,
    IReadOnlyList<WorkoutSetResponse> Sets);

public sealed record WorkoutSetResponse(Guid Id, int Position, int TargetMinReps, int TargetMaxReps);
