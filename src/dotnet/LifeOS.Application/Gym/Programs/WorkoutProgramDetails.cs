using LifeOS.Application.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;

namespace LifeOS.Application.Gym.Programs;

// The whole program as read by the client, every level ordered by position.
public sealed record WorkoutProgramDetails(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<WorkoutDetails> Workouts)
{
    // Exercise names are current metadata, read from the owner's exercises.
    public static async Task<WorkoutProgramDetails> ReadAsync(
        WorkoutProgram program,
        IExerciseRepository exerciseRepository,
        CancellationToken cancellationToken)
    {
        var exerciseIds = program.Workouts
            .SelectMany(workout => workout.Blocks)
            .SelectMany(block => block.Exercises)
            .Select(exercise => exercise.ExerciseId)
            .Distinct()
            .ToList();

        var names = exerciseIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await exerciseRepository.GetByIdsAsync(program.UserId, exerciseIds, cancellationToken))
                .ToDictionary(exercise => exercise.Id, exercise => exercise.Name);

        return new WorkoutProgramDetails(
            program.Id,
            program.Name,
            program.CreatedAtUtc,
            program.Workouts
                .Select(workout => new WorkoutDetails(
                    workout.Id,
                    workout.Name,
                    workout.Position,
                    workout.Blocks
                        .Select(block => new WorkoutBlockDetails(
                            block.Id,
                            block.Position,
                            block.Kind,
                            block.RestSeconds,
                            block.Exercises
                                .Select(exercise => new WorkoutBlockExerciseDetails(
                                    exercise.Id,
                                    exercise.Position,
                                    exercise.ExerciseId,
                                    // The exercise foreign key guarantees the owner's exercise exists.
                                    names[exercise.ExerciseId],
                                    exercise.Notes,
                                    exercise.Sets
                                        .Select(set => new WorkoutSetDetails(set.Id, set.Position, set.TargetMinReps, set.TargetMaxReps))
                                        .ToList()))
                                .ToList()))
                        .ToList()))
                .ToList());
    }
}

public sealed record WorkoutDetails(Guid Id, string Name, int Position, IReadOnlyList<WorkoutBlockDetails> Blocks);

public sealed record WorkoutBlockDetails(
    Guid Id,
    int Position,
    WorkoutBlockKind Kind,
    int? RestSeconds,
    IReadOnlyList<WorkoutBlockExerciseDetails> Exercises);

public sealed record WorkoutBlockExerciseDetails(
    Guid Id,
    int Position,
    Guid ExerciseId,
    string ExerciseName,
    string? Notes,
    IReadOnlyList<WorkoutSetDetails> Sets);

public sealed record WorkoutSetDetails(Guid Id, int Position, int TargetMinReps, int TargetMaxReps);
