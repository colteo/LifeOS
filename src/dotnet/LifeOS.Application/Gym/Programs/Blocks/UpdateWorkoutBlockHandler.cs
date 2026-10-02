using LifeOS.Application.Gym.Exercises;

namespace LifeOS.Application.Gym.Programs.Blocks;

// The kind is not part of an update: a block stays Single or Superset.
public sealed record UpdateWorkoutBlockCommand(
    Guid ProgramId,
    Guid WorkoutId,
    Guid BlockId,
    int? RestSeconds,
    IReadOnlyList<WorkoutBlockExerciseInput>? Exercises);

// Replaces a block's prescription: its rest and, per exercise slot, the exercise, notes and sets.
public sealed class UpdateWorkoutBlockHandler
{
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly IExerciseRepository _exerciseRepository;

    public UpdateWorkoutBlockHandler(IWorkoutProgramRepository programRepository, IExerciseRepository exerciseRepository)
    {
        _programRepository = programRepository;
        _exerciseRepository = exerciseRepository;
    }

    // Throws ArgumentException for an invalid prescription (exercise count, sets, reps, rest, notes).
    public Task<WorkoutProgramEditResult> HandleAsync(
        Guid userId,
        UpdateWorkoutBlockCommand command,
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

                var block = workout.FindBlock(command.BlockId);

                if (block is null)
                {
                    return WorkoutProgramEditStatus.BlockNotFound;
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

                block.Update(command.RestSeconds, exercises);

                return WorkoutProgramEditStatus.Updated;
            },
            cancellationToken);
}
