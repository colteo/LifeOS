using LifeOS.Domain.Gym.Training;

namespace LifeOS.Application.Gym.Training;

// Persists the ActiveProgram aggregate with its completions. Every read is scoped to the owner.
public interface IActiveProgramRepository
{
    // Returns false, persisting nothing, when the user already has an Active program (e.g. one
    // activated concurrently). The database enforces at most one per user.
    Task<bool> TryAddAsync(ActiveProgram program, CancellationToken cancellationToken);

    // The user's Active program, if any, for reading only.
    Task<ActiveProgram?> GetActiveAsync(Guid userId, CancellationToken cancellationToken);

    // The user's Active program, for a change followed by SaveAsync. Changes are serialized: a
    // concurrent GetActiveForUpdateAsync waits until this change is stored or its request ends. When a
    // change of another aggregate is already in progress in the same request (its transaction), this
    // joins it, so both are stored atomically when that aggregate is saved.
    Task<ActiveProgram?> GetActiveForUpdateAsync(Guid userId, CancellationToken cancellationToken);

    // Stores every change made to a program returned by GetActiveForUpdateAsync.
    Task SaveAsync(ActiveProgram program, CancellationToken cancellationToken);
}
