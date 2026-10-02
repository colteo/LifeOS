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
}
