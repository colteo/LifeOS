using LifeOS.Application.Gym.Exercises;

namespace LifeOS.Application.Gym.Programs.Blocks;

// Deletes one block with its prescriptions; the remaining blocks keep their order.
public sealed class DeleteWorkoutBlockHandler
{
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly IExerciseRepository _exerciseRepository;

    public DeleteWorkoutBlockHandler(IWorkoutProgramRepository programRepository, IExerciseRepository exerciseRepository)
    {
        _programRepository = programRepository;
        _exerciseRepository = exerciseRepository;
    }

    public Task<WorkoutProgramEditResult> HandleAsync(
        Guid userId,
        Guid programId,
        Guid workoutId,
        Guid blockId,
        CancellationToken cancellationToken) =>
        WorkoutProgramEdit.RunAsync(
            _programRepository,
            _exerciseRepository,
            userId,
            programId,
            program =>
            {
                var workout = program.FindWorkout(workoutId);

                if (workout is null)
                {
                    return WorkoutProgramEditStatus.WorkoutNotFound;
                }

                return workout.RemoveBlock(blockId)
                    ? WorkoutProgramEditStatus.Updated
                    : WorkoutProgramEditStatus.BlockNotFound;
            },
            cancellationToken);
}
