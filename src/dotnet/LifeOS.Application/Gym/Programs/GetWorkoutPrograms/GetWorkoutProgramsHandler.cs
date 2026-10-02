namespace LifeOS.Application.Gym.Programs.GetWorkoutPrograms;

public sealed record WorkoutProgramSummary(Guid Id, string Name, int WorkoutCount, DateTimeOffset CreatedAtUtc);

public sealed class GetWorkoutProgramsHandler
{
    private readonly IWorkoutProgramRepository _programRepository;

    public GetWorkoutProgramsHandler(IWorkoutProgramRepository programRepository)
    {
        _programRepository = programRepository;
    }

    public async Task<IReadOnlyList<WorkoutProgramSummary>> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var programs = await _programRepository.GetSummariesAsync(userId, cancellationToken);

        // Deterministic order: name ignoring case, then id.
        return programs
            .OrderBy(program => program.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(program => program.Id)
            .ToList();
    }
}
