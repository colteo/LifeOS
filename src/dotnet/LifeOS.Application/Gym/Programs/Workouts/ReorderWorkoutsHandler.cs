using LifeOS.Application.Gym.Exercises;

namespace LifeOS.Application.Gym.Programs.Workouts;

public sealed class ReorderWorkoutsHandler
{
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly IExerciseRepository _exerciseRepository;

    public ReorderWorkoutsHandler(IWorkoutProgramRepository programRepository, IExerciseRepository exerciseRepository)
    {
        _programRepository = programRepository;
        _exerciseRepository = exerciseRepository;
    }

    // Throws ArgumentException unless orderedWorkoutIds lists every workout of the program exactly once.
    public Task<WorkoutProgramEditResult> HandleAsync(
        Guid userId,
        Guid programId,
        IReadOnlyList<Guid> orderedWorkoutIds,
        CancellationToken cancellationToken) =>
        WorkoutProgramEdit.RunAsync(
            _programRepository,
            _exerciseRepository,
            userId,
            programId,
            program =>
            {
                program.ReorderWorkouts(orderedWorkoutIds);

                return WorkoutProgramEditStatus.Updated;
            },
            cancellationToken);
}
