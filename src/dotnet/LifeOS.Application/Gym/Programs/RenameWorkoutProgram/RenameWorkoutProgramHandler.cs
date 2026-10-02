using LifeOS.Application.Gym.Exercises;

namespace LifeOS.Application.Gym.Programs.RenameWorkoutProgram;

public sealed class RenameWorkoutProgramHandler
{
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly IExerciseRepository _exerciseRepository;

    public RenameWorkoutProgramHandler(IWorkoutProgramRepository programRepository, IExerciseRepository exerciseRepository)
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
                program.Rename(name);

                return WorkoutProgramEditStatus.Updated;
            },
            cancellationToken);
}
