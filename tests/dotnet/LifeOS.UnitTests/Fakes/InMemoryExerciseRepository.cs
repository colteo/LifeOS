using System.Reflection;
using LifeOS.Application.Gym.Exercises;
using LifeOS.Domain.Gym.Exercises;

namespace LifeOS.UnitTests.Fakes;

// Every read filters by userId, like the EF Core repository; ownership tests depend on it. Writes
// enforce the same rule as the ux_exercises_user_name index.
internal sealed class InMemoryExerciseRepository : IExerciseRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Lock _lock = new();

    public List<Exercise> Exercises { get; } = [];

    // Runs just before an add checks for conflicts, to simulate a concurrent insert.
    public Action? BeforeAdd { get; set; }

    public Task<bool> TryAddAsync(Exercise exercise, CancellationToken cancellationToken)
    {
        BeforeAdd?.Invoke();

        lock (_lock)
        {
            if (Exercises.Any(stored =>
                    stored.UserId == exercise.UserId
                    && string.Equals(stored.Name, exercise.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.FromResult(false);
            }

            Exercises.Add(Clone(exercise));

            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<Exercise>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Exercise>>(
                Exercises.Where(exercise => exercise.UserId == userId).Select(Clone).ToList());
        }
    }

    public Task<IReadOnlyList<Exercise>> GetByIdsAsync(
        Guid userId,
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Exercise>>(Exercises
                .Where(exercise => exercise.UserId == userId && ids.Contains(exercise.Id))
                .Select(Clone)
                .ToList());
        }
    }

    // Test setup: stores an exercise directly.
    public Exercise Add(Guid userId, string name)
    {
        var exercise = Exercise.Create(userId, name, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));

        lock (_lock)
        {
            Exercises.Add(exercise);
        }

        return Clone(exercise);
    }

    private static Exercise Clone(Exercise exercise) => (Exercise)CloneMethod.Invoke(exercise, null)!;
}
