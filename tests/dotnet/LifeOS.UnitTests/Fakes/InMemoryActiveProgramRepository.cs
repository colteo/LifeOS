using System.Collections;
using System.Reflection;
using LifeOS.Application.Gym.Training;
using LifeOS.Domain.Gym.Training;

namespace LifeOS.UnitTests.Fakes;

// Stores whole active programs. Every read filters by userId, like the EF Core repository. Reads
// return deep copies, like a fresh DbContext, so a handler's changes are only "stored" when it calls
// SaveAsync. Adds enforce the same rule as ux_active_programs_user_active.
internal sealed class InMemoryActiveProgramRepository : IActiveProgramRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Lock _lock = new();

    public List<ActiveProgram> Programs { get; } = [];

    public Task<bool> TryAddAsync(ActiveProgram program, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (Programs.Any(stored => stored.UserId == program.UserId && stored.Status == ActiveProgramStatus.Active))
            {
                return Task.FromResult(false);
            }

            Programs.Add(DeepClone(program));

            return Task.FromResult(true);
        }
    }

    public Task<ActiveProgram?> GetActiveAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(stored => stored.UserId == userId && stored.Status == ActiveProgramStatus.Active));

    public Task<ActiveProgram?> GetActiveForUpdateAsync(Guid userId, CancellationToken cancellationToken) =>
        GetActiveAsync(userId, cancellationToken);

    public Task SaveAsync(ActiveProgram program, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = Programs.FindIndex(stored => stored.Id == program.Id && stored.UserId == program.UserId);

            if (index >= 0)
            {
                Programs[index] = DeepClone(program);
            }
        }

        return Task.CompletedTask;
    }

    // The stored program, for assertions.
    public ActiveProgram Stored(Guid id) => Find(stored => stored.Id == id)!;

    private ActiveProgram? Find(Func<ActiveProgram, bool> predicate)
    {
        lock (_lock)
        {
            var program = Programs.SingleOrDefault(predicate);

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
