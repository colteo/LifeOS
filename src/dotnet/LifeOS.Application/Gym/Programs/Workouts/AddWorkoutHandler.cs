using LifeOS.Application.Gym.Exercises;

namespace LifeOS.Application.Gym.Programs.Workouts;

// Appends a workout to the program.
public sealed class AddWorkoutHandler
{
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly IExerciseRepository _exerciseRepository;

    public AddWorkoutHandler(IWorkoutProgramRepository programRepository, IExerciseRepository exerciseRepository)
    {
        _programRepository = programRepository;
        _exerciseRepository = exerciseRepository;
    }

    // Throws ArgumentException for a blank name.
    public Task<WorkoutProgramEditResult> HandleAsync(
        Guid userId,
        Guid programId,
        string name,
        CancellationToken cancellationToken) =>
        WorkoutProgramEdit.RunAsync(
            _programRepository,
            _exerciseRepository,
            userId,
            programId,
            program =>
            {
                program.AddWorkout(name);

                return WorkoutProgramEditStatus.Updated;
            },
            cancellationToken);
}
