using LifeOS.Domain.Gym.Exercises;

namespace LifeOS.Application.Gym.Exercises.CreateExercise;

// Creates a reusable exercise. Names are unique per user, ignoring case; other users' exercises
// never conflict.
public sealed class CreateExerciseHandler
{
    private readonly IExerciseRepository _exerciseRepository;
    private readonly TimeProvider _timeProvider;

    public CreateExerciseHandler(IExerciseRepository exerciseRepository, TimeProvider timeProvider)
    {
        _exerciseRepository = exerciseRepository;
        _timeProvider = timeProvider;
    }

    // Throws ArgumentException for a blank name.
    public async Task<CreateExerciseResult> HandleAsync(Guid userId, string name, CancellationToken cancellationToken)
    {
        var exercise = Exercise.Create(userId, name, _timeProvider.GetUtcNow());

        var existing = await _exerciseRepository.GetAllAsync(userId, cancellationToken);

        if (existing.Any(other => string.Equals(other.Name, exercise.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return CreateExerciseResult.DuplicateName();
        }

        // The database is the final backstop (concurrent creates, or case rules differing from C#).
        if (!await _exerciseRepository.TryAddAsync(exercise, cancellationToken))
        {
            return CreateExerciseResult.DuplicateName();
        }

        return CreateExerciseResult.Created(new ExerciseSummary(exercise.Id, exercise.Name, exercise.CreatedAtUtc));
    }
}

public enum CreateExerciseStatus
{
    Created,
    DuplicateName
}

public sealed record CreateExerciseResult(CreateExerciseStatus Status, ExerciseSummary? Exercise)
{
    public static CreateExerciseResult Created(ExerciseSummary exercise) => new(CreateExerciseStatus.Created, exercise);

    public static CreateExerciseResult DuplicateName() => new(CreateExerciseStatus.DuplicateName, null);
}
