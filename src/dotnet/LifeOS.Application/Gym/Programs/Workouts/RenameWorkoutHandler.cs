using LifeOS.Application.Gym.Exercises;

namespace LifeOS.Application.Gym.Programs.Workouts;

public sealed class RenameWorkoutHandler
{
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly IExerciseRepository _exerciseRepository;

    public RenameWorkoutHandler(IWorkoutProgramRepository programRepository, IExerciseRepository exerciseRepository)
    {
        _programRepository = programRepository;
        _exerciseRepository = exerciseRepository;
    }

    // Throws ArgumentException for a blank name.
    public Task<WorkoutProgramEditResult> HandleAsync(
        Guid userId,
        Guid programId,
        Guid workoutId,
        string name,
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

                workout.Rename(name);

                return WorkoutProgramEditStatus.Updated;
            },
            cancellationToken);
}
