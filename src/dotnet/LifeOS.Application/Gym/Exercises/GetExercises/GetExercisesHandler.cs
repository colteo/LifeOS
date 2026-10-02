namespace LifeOS.Application.Gym.Exercises.GetExercises;

public sealed class GetExercisesHandler
{
    private readonly IExerciseRepository _exerciseRepository;

    public GetExercisesHandler(IExerciseRepository exerciseRepository)
    {
        _exerciseRepository = exerciseRepository;
    }

    public async Task<IReadOnlyList<ExerciseSummary>> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        var exercises = await _exerciseRepository.GetAllAsync(userId, cancellationToken);

        // Deterministic order: name ignoring case, then id.
        return exercises
            .OrderBy(exercise => exercise.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(exercise => exercise.Id)
            .Select(exercise => new ExerciseSummary(exercise.Id, exercise.Name, exercise.CreatedAtUtc))
            .ToList();
    }
}
