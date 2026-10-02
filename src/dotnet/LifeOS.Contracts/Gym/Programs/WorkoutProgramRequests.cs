namespace LifeOS.Contracts.Gym.Programs;

public sealed record CreateWorkoutProgramRequest(string Name);

public sealed record UpdateWorkoutProgramRequest(string Name);

public sealed record CreateWorkoutRequest(string Name);

public sealed record UpdateWorkoutRequest(string Name);

// Every workout of the program exactly once, in the new order.
public sealed record ReorderWorkoutsRequest(IReadOnlyList<Guid> WorkoutIds);

// Kind is "Single" (exactly one exercise) or "Superset" (exactly two: A, then B). RestSeconds is the
// rest after each set (Single) or each A+B round (Superset); null when not prescribed.
public sealed record CreateWorkoutBlockRequest(
    string Kind,
    int? RestSeconds,
    IReadOnlyList<WorkoutBlockExerciseRequest> Exercises);

// The kind of a block cannot change; the exercises are given per slot, in order.
public sealed record UpdateWorkoutBlockRequest(
    int? RestSeconds,
    IReadOnlyList<WorkoutBlockExerciseRequest> Exercises);

public sealed record WorkoutBlockExerciseRequest(
    Guid ExerciseId,
    string? Notes,
    IReadOnlyList<WorkoutSetRequest> Sets);

// One set, in order: 8 reps is 8-8, a range is e.g. 8-10.
public sealed record WorkoutSetRequest(int TargetMinReps, int TargetMaxReps);

// Every block of the workout exactly once, in the new order.
public sealed record ReorderWorkoutBlocksRequest(IReadOnlyList<Guid> BlockIds);
