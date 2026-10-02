using LifeOS.Application.Gym.Exercises;

namespace LifeOS.Application.Gym.Programs.GetWorkoutProgram;

public sealed class GetWorkoutProgramHandler
{
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly IExerciseRepository _exerciseRepository;

    public GetWorkoutProgramHandler(IWorkoutProgramRepository programRepository, IExerciseRepository exerciseRepository)
    {
        _programRepository = programRepository;
        _exerciseRepository = exerciseRepository;
    }

    // Null for a missing program or another user's program.
    public async Task<WorkoutProgramDetails?> HandleAsync(Guid userId, Guid programId, CancellationToken cancellationToken)
    {
        var program = await _programRepository.GetAsync(userId, programId, cancellationToken);

        return program is null
            ? null
            : await WorkoutProgramDetails.ReadAsync(program, _exerciseRepository, cancellationToken);
    }
}
