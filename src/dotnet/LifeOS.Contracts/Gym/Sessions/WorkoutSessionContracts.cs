namespace LifeOS.Contracts.Gym.Sessions;

// Starts the given workout of one of the caller's programs.
public sealed record StartWorkoutSessionRequest(Guid ProgramId, Guid WorkoutId);

// Completes a set, or corrects it while the workout is in progress. WeightKg is optional: null means
// no external load (bodyweight); otherwise more than 0, at most 1000, with at most two decimals.
public sealed record RecordWorkoutSetRequest(int ActualReps, decimal? WeightKg);

// The whole workout session, every list ordered by position. Status is "InProgress" or "Completed".
// Rest is the prescribed rest started by the latest completed set, or null; ServerTimeUtc is the
// server clock when the response was produced, to align client timers.
public sealed record WorkoutSessionResponse(
    Guid Id,
    Guid? ProgramId,
    Guid? WorkoutId,
    string ProgramName,
    string WorkoutName,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int CompletedSetCount,
    int PrescribedSetCount,
    WorkoutRestResponse? Rest,
    DateTimeOffset ServerTimeUtc,
    IReadOnlyList<WorkoutSessionBlockResponse> Blocks);

public sealed record WorkoutRestResponse(DateTimeOffset StartedAtUtc, DateTimeOffset EndsAtUtc, int Seconds);

// Kind is "Single" or "Superset"; RestSeconds is the rest after each set (Single) or round (Superset).
public sealed record WorkoutSessionBlockResponse(
    Guid Id,
    int Position,
    string Kind,
    int? RestSeconds,
    IReadOnlyList<WorkoutSessionExerciseResponse> Exercises);

public sealed record WorkoutSessionExerciseResponse(
    Guid Id,
    int Position,
    Guid ExerciseId,
    string ExerciseName,
    string? Notes,
    IReadOnlyList<WorkoutSessionSetResponse> Sets);

// Pending while CompletedAtUtc is null.
public sealed record WorkoutSessionSetResponse(
    Guid Id,
    int Position,
    int TargetMinReps,
    int TargetMaxReps,
    int? ActualReps,
    decimal? WeightKg,
    DateTimeOffset? CompletedAtUtc);
