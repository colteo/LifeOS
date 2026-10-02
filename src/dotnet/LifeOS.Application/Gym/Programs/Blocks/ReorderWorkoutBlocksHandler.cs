using LifeOS.Application.Gym.Exercises;

namespace LifeOS.Application.Gym.Programs.Blocks;

public sealed class ReorderWorkoutBlocksHandler
{
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly IExerciseRepository _exerciseRepository;

    public ReorderWorkoutBlocksHandler(IWorkoutProgramRepository programRepository, IExerciseRepository exerciseRepository)
    {
        _programRepository = programRepository;
        _exerciseRepository = exerciseRepository;
    }

    // Throws ArgumentException unless orderedBlockIds lists every block of the workout exactly once.
    public Task<WorkoutProgramEditResult> HandleAsync(
        Guid userId,
        Guid programId,
        Guid workoutId,
        IReadOnlyList<Guid> orderedBlockIds,
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

                workout.ReorderBlocks(orderedBlockIds);

                return WorkoutProgramEditStatus.Updated;
            },
            cancellationToken);
}
