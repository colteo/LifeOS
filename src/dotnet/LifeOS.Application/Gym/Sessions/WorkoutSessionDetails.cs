using LifeOS.Application.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;

namespace LifeOS.Application.Gym.Sessions;

// The whole session as read by the client, every level ordered by position. ServerTimeUtc is the
// server clock when it was read, so the client can align elapsed time and rest with its own clock.
public sealed record WorkoutSessionDetails(
    Guid Id,
    Guid? ProgramId,
    Guid? WorkoutId,
    string ProgramName,
    string WorkoutName,
    WorkoutSessionStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int CompletedSetCount,
    int PrescribedSetCount,
    RestPeriod? Rest,
    DateTimeOffset ServerTimeUtc,
    IReadOnlyList<WorkoutSessionBlockDetails> Blocks)
{
    // Exercise names are current metadata, read from the owner's exercises.
    public static async Task<WorkoutSessionDetails> ReadAsync(
        WorkoutSession session,
        IExerciseRepository exerciseRepository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var exerciseIds = session.Blocks
            .SelectMany(block => block.Exercises)
            .Select(exercise => exercise.ExerciseId)
            .Distinct()
            .ToList();

        var names = (await exerciseRepository.GetByIdsAsync(session.UserId, exerciseIds, cancellationToken))
            .ToDictionary(exercise => exercise.Id, exercise => exercise.Name);

        return new WorkoutSessionDetails(
            session.Id,
            session.WorkoutProgramId,
            session.WorkoutTemplateId,
            session.ProgramName,
            session.WorkoutName,
            session.Status,
            session.StartedAtUtc,
            session.CompletedAtUtc,
            session.CompletedSetCount,
            session.PrescribedSetCount,
            session.CurrentRest(),
            timeProvider.GetUtcNow(),
            session.Blocks
                .Select(block => new WorkoutSessionBlockDetails(
                    block.Id,
                    block.Position,
                    block.Kind,
                    block.RestSeconds,
                    block.Exercises
                        .Select(exercise => new WorkoutSessionExerciseDetails(
                            exercise.Id,
                            exercise.Position,
                            exercise.ExerciseId,
                            // The exercise foreign key (RESTRICT) guarantees the owner's exercise exists.
                            names[exercise.ExerciseId],
                            exercise.Notes,
                            exercise.Sets
                                .Select(set => new WorkoutSessionSetDetails(
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
    }
}

public sealed record WorkoutSessionBlockDetails(
    Guid Id,
    int Position,
    WorkoutBlockKind Kind,
    int? RestSeconds,
    IReadOnlyList<WorkoutSessionExerciseDetails> Exercises);

public sealed record WorkoutSessionExerciseDetails(
    Guid Id,
    int Position,
    Guid ExerciseId,
    string ExerciseName,
    string? Notes,
    IReadOnlyList<WorkoutSessionSetDetails> Sets);

public sealed record WorkoutSessionSetDetails(
    Guid Id,
    int Position,
    int TargetMinReps,
    int TargetMaxReps,
    int? ActualReps,
    decimal? WeightKg,
    DateTimeOffset? CompletedAtUtc);
