using LifeOS.Domain.Gym.Programs;

namespace LifeOS.Application.Gym.Programs.CreateWorkoutProgram;

public sealed class CreateWorkoutProgramHandler
{
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly TimeProvider _timeProvider;

    public CreateWorkoutProgramHandler(IWorkoutProgramRepository programRepository, TimeProvider timeProvider)
    {
        _programRepository = programRepository;
        _timeProvider = timeProvider;
    }

    // Throws ArgumentException for a blank name. A new program has no workouts.
    public async Task<WorkoutProgramDetails> HandleAsync(Guid userId, string name, CancellationToken cancellationToken)
    {
        var program = WorkoutProgram.Create(userId, name, _timeProvider.GetUtcNow());

        await _programRepository.AddAsync(program, cancellationToken);

        return new WorkoutProgramDetails(program.Id, program.Name, program.CreatedAtUtc, []);
    }
}
