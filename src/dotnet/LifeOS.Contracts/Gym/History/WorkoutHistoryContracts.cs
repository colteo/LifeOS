namespace LifeOS.Contracts.Gym.History;

// One page of completed workouts, newest completed first. NextCursor continues the list (pass it as
// "cursor"); null on the last page.
public sealed record WorkoutHistoryPageResponse(IReadOnlyList<WorkoutHistoryItemResponse> Items, string? NextCursor);

// A completed workout from its snapshot: the program and workout names as they were when it started.
// ExerciseCount counts exercise prescriptions (a superset counts 2).
public sealed record WorkoutHistoryItemResponse(
    Guid Id,
    string ProgramName,
    string WorkoutName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int CompletedSetCount,
    int PrescribedSetCount,
    int ExerciseCount);

// For the exercises of a session, what was recorded the last time each was done: one previous
// completed session per exercise. Exercises without one are absent.
public sealed record PreviousPerformanceResponse(Guid SessionId, IReadOnlyList<PreviousExercisePerformanceResponse> Exercises);

// SessionId, WorkoutName and CompletedAtUtc describe the previous session. Sets are its recorded sets
// of the exercise in execution order; an exercise done in more than one block has one group per
// BlockPosition.
public sealed record PreviousExercisePerformanceResponse(
    Guid ExerciseId,
    Guid SessionId,
    string WorkoutName,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<PreviousSetResponse> Sets);

// WeightKg null: no external load (bodyweight).
public sealed record PreviousSetResponse(int BlockPosition, int Position, int ActualReps, decimal? WeightKg);

// One page of an exercise's earlier completed workouts, newest completed first. NextCursor continues
// the list (pass it as "cursor"); null on the last page.
public sealed record ExerciseHistoryPageResponse(Guid ExerciseId, IReadOnlyList<ExerciseHistoryEntryResponse> Items, string? NextCursor);

// One earlier completed workout that contained the exercise, with its recorded sets of that exercise
// in execution order (one group per BlockPosition when it was done in several blocks).
public sealed record ExerciseHistoryEntryResponse(
    Guid SessionId,
    string ProgramName,
    string WorkoutName,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<PreviousSetResponse> Sets);
