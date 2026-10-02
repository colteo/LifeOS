namespace LifeOS.Contracts.Gym.Training;

// A program as a choice of workouts to train. Workouts are ordered by position.
public sealed record TrainingProgramResponse(Guid Id, string Name, IReadOnlyList<TrainingWorkoutResponse> Workouts);

// ExerciseCount counts both exercises of a superset. CanStart is false for a workout without
// exercises, which the start endpoint would refuse.
public sealed record TrainingWorkoutResponse(
    Guid Id,
    string Name,
    int Position,
    int ExerciseCount,
    int PrescribedSetCount,
    bool CanStart);
