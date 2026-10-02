using LifeOS.Application.Gym.Programs.GetWorkoutPrograms;
using LifeOS.Application.Gym.Training;
using LifeOS.Domain.Gym.Programs;

namespace LifeOS.Application.Gym.Programs;

// Persists the WorkoutProgram aggregate as a whole: the program with its workouts, blocks, exercise
// prescriptions and set prescriptions. Every read is scoped to the owner; another user's program is
// indistinguishable from a missing one.
public interface IWorkoutProgramRepository
{
    Task AddAsync(WorkoutProgram program, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkoutProgramSummary>> GetSummariesAsync(Guid userId, CancellationToken cancellationToken);

    // Every program with its workouts' block, exercise and set counts, for choosing a workout to train.
    Task<IReadOnlyList<TrainingProgram>> GetTrainingProgramsAsync(Guid userId, CancellationToken cancellationToken);

    // The whole program, for reading only.
    Task<WorkoutProgram?> GetAsync(Guid userId, Guid programId, CancellationToken cancellationToken);

    // The whole program, for an edit followed by SaveAsync. Edits of the same program are serialized:
    // a concurrent GetForUpdateAsync waits until this edit is saved or its request ends, then sees the
    // saved state.
    Task<WorkoutProgram?> GetForUpdateAsync(Guid userId, Guid programId, CancellationToken cancellationToken);

    // Stores every change made to a program returned by GetForUpdateAsync.
    Task SaveAsync(WorkoutProgram program, CancellationToken cancellationToken);

    // Deletes the program with its workouts, blocks and prescriptions. Exercises are never deleted.
    // Returns false when the user has no such program.
    Task<bool> DeleteAsync(Guid userId, Guid programId, CancellationToken cancellationToken);
}
