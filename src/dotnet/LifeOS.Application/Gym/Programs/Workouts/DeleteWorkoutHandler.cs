using LifeOS.Application.Gym.Exercises;

namespace LifeOS.Application.Gym.Programs.Workouts;

// Deletes one workout with its blocks and prescriptions; the remaining workouts keep their order.
public sealed class DeleteWorkoutHandler
{
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly IExerciseRepository _exerciseRepository;

    public DeleteWorkoutHandler(IWorkoutProgramRepository programRepository, IExerciseRepository exerciseRepository)
    {
        _programRepository = programRepository;
        _exerciseRepository = exerciseRepository;
    }

    public Task<WorkoutProgramEditResult> HandleAsync(
        Guid userId,
        Guid programId,
        Guid workoutId,
        CancellationToken cancellationToken) =>
        WorkoutProgramEdit.RunAsync(
            _programRepository,
            _exerciseRepository,
            userId,
            programId,
            program => program.RemoveWorkout(workoutId)
                ? WorkoutProgramEditStatus.Updated
                : WorkoutProgramEditStatus.WorkoutNotFound,
            cancellationToken);
}
