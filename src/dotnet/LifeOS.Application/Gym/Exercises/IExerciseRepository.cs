using LifeOS.Domain.Gym.Exercises;

namespace LifeOS.Application.Gym.Exercises;

public interface IExerciseRepository
{
    // Returns false, persisting nothing, when the user already has an exercise with the same name
    // (ignoring case), e.g. created concurrently. The database enforces it.
    Task<bool> TryAddAsync(Exercise exercise, CancellationToken cancellationToken);

    Task<IReadOnlyList<Exercise>> GetAllAsync(Guid userId, CancellationToken cancellationToken);

    // Only exercises owned by userId; another user's exercise is indistinguishable from a missing one.
    Task<IReadOnlyList<Exercise>> GetByIdsAsync(Guid userId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
}
