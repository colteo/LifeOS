using System.Collections;
using System.Reflection;
using LifeOS.Application.Gym.History;
using LifeOS.Application.Gym.Sessions;
using LifeOS.Domain.Gym.Sessions;

namespace LifeOS.UnitTests.Fakes;

// Stores whole sessions. Every read filters by userId, like the EF Core repository; ownership tests
// depend on it. Reads return deep copies, like a fresh DbContext, so a handler's changes are only
// "stored" when it calls SaveAsync. Adds enforce the same rule as ux_workout_sessions_user_in_progress.
internal sealed class InMemoryWorkoutSessionRepository : IWorkoutSessionRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Lock _lock = new();

    public List<WorkoutSession> Sessions { get; } = [];

    // How many times previous performance was read, to show one read serves every exercise.
    public int PreviousPerformanceReads { get; private set; }

    // Runs just before an add checks for an InProgress session, to simulate a concurrent start.
    public Action? BeforeAdd { get; set; }

    public Task<bool> TryAddAsync(WorkoutSession session, CancellationToken cancellationToken)
    {
        BeforeAdd?.Invoke();

        lock (_lock)
        {
            if (session.Status == WorkoutSessionStatus.InProgress
                && Sessions.Any(stored => stored.UserId == session.UserId && stored.Status == WorkoutSessionStatus.InProgress))
            {
                return Task.FromResult(false);
            }

            Sessions.Add(DeepClone(session));

            return Task.FromResult(true);
        }
    }

    public Task<WorkoutSession?> GetAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(stored => stored.UserId == userId && stored.Id == sessionId));

    public Task<WorkoutSession?> GetInProgressAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(stored => stored.UserId == userId && stored.Status == WorkoutSessionStatus.InProgress));

    public Task<WorkoutSession?> GetForUpdateAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
        GetAsync(userId, sessionId, cancellationToken);

    // Replaces the stored session; a session deleted in the meantime stays deleted.
    public Task SaveAsync(WorkoutSession session, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = Sessions.FindIndex(stored => stored.Id == session.Id && stored.UserId == session.UserId);

            if (index >= 0)
            {
                Sessions[index] = DeepClone(session);
            }
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(WorkoutSession session, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Sessions.RemoveAll(stored => stored.Id == session.Id && stored.UserId == session.UserId);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WorkoutHistoryItem>> GetCompletedPageAsync(
        Guid userId,
        WorkoutHistoryCursor? after,
        int take,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<WorkoutHistoryItem> page = Sessions
                .Where(session => session.UserId == userId && session.Status == WorkoutSessionStatus.Completed)
                .Where(session => after is null
                    || session.CompletedAtUtc < after.CompletedAtUtc
                    || (session.CompletedAtUtc == after.CompletedAtUtc && session.Id.CompareTo(after.Id) < 0))
                .OrderByDescending(session => session.CompletedAtUtc)
                .ThenByDescending(session => session.Id)
                .Take(take)
                .Select(session => new WorkoutHistoryItem(
                    session.Id,
                    session.ProgramName,
                    session.WorkoutName,
                    session.StartedAtUtc,
                    session.CompletedAtUtc!.Value,
                    session.CompletedSetCount,
                    session.PrescribedSetCount,
                    session.Blocks.Sum(block => block.Exercises.Count)))
                .ToList();

            return Task.FromResult(page);
        }
    }

    public Task<IReadOnlyList<PreviousExercisePerformance>> GetPreviousPerformancesAsync(
        Guid userId,
        IReadOnlyCollection<Guid> exerciseIds,
        DateTimeOffset completedBefore,
        Guid excludingSessionId,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            PreviousPerformanceReads++;

            var candidates = Sessions
                .Where(session => session.UserId == userId
                    && session.Status == WorkoutSessionStatus.Completed
                    && session.CompletedAtUtc < completedBefore
                    && session.Id != excludingSessionId)
                .ToList();

            IReadOnlyList<PreviousExercisePerformance> result = exerciseIds
                .Distinct()
                .Select(exerciseId => (
                    ExerciseId: exerciseId,
                    Session: candidates
                        .Where(session => session.Blocks.Any(block => block.Exercises.Any(exercise => exercise.ExerciseId == exerciseId)))
                        .OrderByDescending(session => session.CompletedAtUtc)
                        .ThenByDescending(session => session.Id)
                        .FirstOrDefault()))
                .Where(item => item.Session is not null)
                .Select(item => new PreviousExercisePerformance(
                    item.ExerciseId,
                    item.Session!.Id,
                    item.Session.WorkoutName,
                    item.Session.CompletedAtUtc!.Value,
                    item.Session.Blocks
                        .SelectMany(block => block.Exercises
                            .Where(exercise => exercise.ExerciseId == item.ExerciseId)
                            .SelectMany(exercise => exercise.Sets)
                            .Where(set => set.IsCompleted)
                            .Select(set => new PreviousSet(block.Position, set.Position, set.ActualReps!.Value, set.WeightKg)))
                        .ToList()))
                .ToList();

            return Task.FromResult(result);
        }
    }

    // Fails exercise-history reads, to show the active workout does not depend on them.
    public bool FailExerciseHistory { get; set; }

    public Task<IReadOnlyList<ExerciseHistoryEntry>> GetExerciseHistoryPageAsync(
        Guid userId,
        Guid exerciseId,
        DateTimeOffset completedBefore,
        Guid excludingSessionId,
        WorkoutHistoryCursor? after,
        int take,
        CancellationToken cancellationToken)
    {
        if (FailExerciseHistory)
        {
            throw new InvalidOperationException("Exercise history is unavailable.");
        }

        lock (_lock)
        {
            IReadOnlyList<ExerciseHistoryEntry> page = Sessions
                .Where(session => session.UserId == userId
                    && session.Status == WorkoutSessionStatus.Completed
                    && session.CompletedAtUtc < completedBefore
                    && session.Id != excludingSessionId
                    && session.Blocks.Any(block => block.Exercises.Any(exercise => exercise.ExerciseId == exerciseId)))
                .Where(session => after is null
                    || session.CompletedAtUtc < after.CompletedAtUtc
                    || (session.CompletedAtUtc == after.CompletedAtUtc && session.Id.CompareTo(after.Id) < 0))
                .OrderByDescending(session => session.CompletedAtUtc)
                .ThenByDescending(session => session.Id)
                .Take(take)
                .Select(session => new ExerciseHistoryEntry(
                    session.Id,
                    session.ProgramName,
                    session.WorkoutName,
                    session.CompletedAtUtc!.Value,
                    session.Blocks
                        .SelectMany(block => block.Exercises
                            .Where(exercise => exercise.ExerciseId == exerciseId)
                            .SelectMany(exercise => exercise.Sets)
                            .Where(set => set.IsCompleted)
                            .Select(set => new PreviousSet(block.Position, set.Position, set.ActualReps!.Value, set.WeightKg)))
                        .ToList()))
                .ToList();

            return Task.FromResult(page);
        }
    }

    // The stored session, for assertions; null when there is none.
    public WorkoutSession? Stored(Guid sessionId) => Find(stored => stored.Id == sessionId);

    private WorkoutSession? Find(Func<WorkoutSession, bool> predicate)
    {
        lock (_lock)
        {
            var session = Sessions.SingleOrDefault(predicate);

            return session is null ? null : DeepClone(session);
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
