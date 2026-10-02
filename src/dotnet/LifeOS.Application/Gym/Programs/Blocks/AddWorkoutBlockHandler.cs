using LifeOS.Application.Gym.Exercises;
using LifeOS.Domain.Gym.Programs;

namespace LifeOS.Application.Gym.Programs.Blocks;

public sealed record AddWorkoutBlockCommand(
    Guid ProgramId,
    Guid WorkoutId,
    WorkoutBlockKind Kind,
    int? RestSeconds,
    IReadOnlyList<WorkoutBlockExerciseInput>? Exercises);

// Appends a Single block (one exercise) or a Superset (two exercises, A then B) to a workout.
public sealed class AddWorkoutBlockHandler
{
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly IExerciseRepository _exerciseRepository;

    public AddWorkoutBlockHandler(IWorkoutProgramRepository programRepository, IExerciseRepository exerciseRepository)
    {
        _programRepository = programRepository;
        _exerciseRepository = exerciseRepository;
    }

    // Throws ArgumentException for an invalid prescription (exercise count, sets, reps, rest, notes).
    public Task<WorkoutProgramEditResult> HandleAsync(
        Guid userId,
        AddWorkoutBlockCommand command,
        CancellationToken cancellationToken) =>
        WorkoutProgramEdit.RunAsync(
            _programRepository,
            _exerciseRepository,
            userId,
            command.ProgramId,
            async program =>
            {
                var workout = program.FindWorkout(command.WorkoutId);

                if (workout is null)
                {
                    return WorkoutProgramEditStatus.WorkoutNotFound;
                }

                var exercises = await ExercisePrescriptions.ResolveAsync(
                    userId,
                    command.Exercises,
                    _exerciseRepository,
                    cancellationToken);

                if (exercises is null)
                {
                    return WorkoutProgramEditStatus.ExerciseNotFound;
                }

                workout.AddBlock(command.Kind, command.RestSeconds, exercises);

                return WorkoutProgramEditStatus.Updated;
            },
            cancellationToken);
}
