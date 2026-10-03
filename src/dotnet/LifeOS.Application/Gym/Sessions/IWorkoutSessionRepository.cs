using LifeOS.Application.Gym.History;
using LifeOS.Domain.Gym.Sessions;

namespace LifeOS.Application.Gym.Sessions;

// Persists the WorkoutSession aggregate as a whole: the session with its snapshot blocks, exercises
// and sets. Every read is scoped to the owner; another user's session is indistinguishable from a
// missing one.
public interface IWorkoutSessionRepository
{
    // Returns false, persisting nothing, when the user already has an InProgress session (e.g. one
    // started concurrently). The database enforces at most one per user.
    Task<bool> TryAddAsync(WorkoutSession session, CancellationToken cancellationToken);

    // The whole session, for reading only.
    Task<WorkoutSession?> GetAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);

    // The user's InProgress session, if any, for reading only.
    Task<WorkoutSession?> GetInProgressAsync(Guid userId, CancellationToken cancellationToken);

    // The whole session, for a change followed by SaveAsync or DeleteAsync. Changes of the same
    // session are serialized: a concurrent GetForUpdateAsync waits until this change is stored or its
    // request ends, then sees the stored state.
    Task<WorkoutSession?> GetForUpdateAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken);

    // Stores every change made to a session returned by GetForUpdateAsync.
    Task SaveAsync(WorkoutSession session, CancellationToken cancellationToken);

    // Deletes a session returned by GetForUpdateAsync, with its snapshot.
    Task DeleteAsync(WorkoutSession session, CancellationToken cancellationToken);

    // At most `take` of the user's Completed sessions, ordered by CompletedAtUtc descending then Id
    // descending, starting after the cursor when given. Read from the snapshot only.
    Task<IReadOnlyList<WorkoutHistoryItem>> GetCompletedPageAsync(
        Guid userId,
        WorkoutHistoryCursor? after,
        int take,
        CancellationToken cancellationToken);

    // For each of the exercise ids, the user's most recent Completed session (latest CompletedAtUtc,
    // ties to the larger Id) completed before completedBefore, other than excludingSessionId, that
    // contains the exercise; with every recorded set of that exercise in that session (all
    // occurrences). Exercises without such a session are absent. Never merges sessions.
    Task<IReadOnlyList<PreviousExercisePerformance>> GetPreviousPerformancesAsync(
        Guid userId,
        IReadOnlyCollection<Guid> exerciseIds,
        DateTimeOffset completedBefore,
        Guid excludingSessionId,
        CancellationToken cancellationToken);

    // At most `take` of the user's Completed sessions completed before completedBefore, other than
    // excludingSessionId, that contain the exercise; ordered by CompletedAtUtc descending then Id
    // descending, starting after the cursor when given. Each with every recorded set of that exercise
    // in that session (all occurrences). Read from the snapshot only.
    Task<IReadOnlyList<ExerciseHistoryEntry>> GetExerciseHistoryPageAsync(
        Guid userId,
        Guid exerciseId,
        DateTimeOffset completedBefore,
        Guid excludingSessionId,
        WorkoutHistoryCursor? after,
        int take,
        CancellationToken cancellationToken);
}
