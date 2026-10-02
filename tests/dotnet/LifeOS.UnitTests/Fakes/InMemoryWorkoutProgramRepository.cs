using System.Collections;
using System.Reflection;
using LifeOS.Application.Gym.Programs;
using LifeOS.Application.Gym.Programs.GetWorkoutPrograms;
using LifeOS.Domain.Gym.Programs;

namespace LifeOS.UnitTests.Fakes;

// Stores whole programs. Every read filters by userId, like the EF Core repository; ownership tests
// depend on it. Reads return deep copies, like a fresh DbContext, so a handler's changes are only
// "stored" when it calls SaveAsync. Like the database, a delete removes the program's descendants
// and never touches exercises.
internal sealed class InMemoryWorkoutProgramRepository : IWorkoutProgramRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Lock _lock = new();

    public List<WorkoutProgram> Programs { get; } = [];

    public int Saves { get; private set; }

    public Task AddAsync(WorkoutProgram program, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Programs.Add(DeepClone(program));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkoutProgramSummary>> GetSummariesAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<WorkoutProgramSummary>>(Programs
                .Where(program => program.UserId == userId)
                .Select(program => new WorkoutProgramSummary(program.Id, program.Name, program.Workouts.Count, program.CreatedAtUtc))
                .ToList());
        }
    }

    public Task<WorkoutProgram?> GetAsync(Guid userId, Guid programId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(userId, programId));

    public Task<WorkoutProgram?> GetForUpdateAsync(Guid userId, Guid programId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(userId, programId));

    // Replaces the stored program, like SaveChanges on the tracked aggregate. A program deleted in
    // the meantime stays deleted.
    public Task SaveAsync(WorkoutProgram program, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = Programs.FindIndex(stored => stored.Id == program.Id && stored.UserId == program.UserId);

            if (index >= 0)
            {
                Programs[index] = DeepClone(program);
            }

            Saves++;
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid userId, Guid programId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Programs.RemoveAll(program => program.UserId == userId && program.Id == programId) == 1);
        }
    }

    // The stored program, for assertions.
    public WorkoutProgram Stored(Guid programId)
    {
        lock (_lock)
        {
            return DeepClone(Programs.Single(program => program.Id == programId));
        }
    }

    private WorkoutProgram? Find(Guid userId, Guid programId)
    {
        lock (_lock)
        {
            var program = Programs.SingleOrDefault(program => program.UserId == userId && program.Id == programId);

            return program is null ? null : DeepClone(program);
        }
    }

    // Copies the aggregate: each entity, and each private List<T> of child entities.
    private static T DeepClone<T>(T source) where T : class => (T)DeepCloneObject(source);

    private static object DeepCloneObject(object source)
    {
        var clone = CloneMethod.Invoke(source, null)!;

        foreach (var field in source.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (field.GetValue(source) is IList children && field.FieldType.IsGenericType
                && field.FieldType.GetGenericTypeDefinition() == typeof(List<>))
            {
                var copy = (IList)Activator.CreateInstance(field.FieldType)!;

                foreach (var child in children)
                {
                    copy.Add(DeepCloneObject(child!));
                }

                field.SetValue(clone, copy);
            }
        }

        return clone;
    }
}
