namespace LifeOS.Application.Gym.Programs.DeleteWorkoutProgram;

// Deletes a program with its workouts, blocks and prescriptions. Exercises are reusable and are never
// deleted with a program.
public sealed class DeleteWorkoutProgramHandler
{
    private readonly IWorkoutProgramRepository _programRepository;

    public DeleteWorkoutProgramHandler(IWorkoutProgramRepository programRepository)
    {
        _programRepository = programRepository;
    }

    // False for a missing program or another user's program; nothing is deleted then.
    public Task<bool> HandleAsync(Guid userId, Guid programId, CancellationToken cancellationToken) =>
        _programRepository.DeleteAsync(userId, programId, cancellationToken);
}
