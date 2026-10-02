using LifeOS.Domain.Gym.Programs;

namespace LifeOS.Domain.Gym.Sessions;

// One exercise of a snapshotted block: the stable Exercise id (for future history and analytics),
// its A/B position, the prescription notes and its ordered sets.
public sealed class WorkoutSessionExercise
{
    private readonly List<WorkoutSessionSet> _sets = [];

    private WorkoutSessionExercise(Guid id, Guid workoutSessionBlockId, Guid userId, Guid exerciseId, int position, string? notes)
    {
        Id = id;
        WorkoutSessionBlockId = workoutSessionBlockId;
        UserId = userId;
        ExerciseId = exerciseId;
        Position = position;
        Notes = notes;
    }

    public Guid Id { get; }

    public Guid WorkoutSessionBlockId { get; }

    // The session's owner; the referenced Exercise must belong to the same user.
    public Guid UserId { get; }

    public Guid ExerciseId { get; }

    // 1 for a Single block; 1 (A) or 2 (B) in a Superset.
    public int Position { get; }

    public string? Notes { get; }

    public IReadOnlyList<WorkoutSessionSet> Sets => _sets.OrderBy(set => set.Position).ToList();

    internal static WorkoutSessionExercise Snapshot(WorkoutSessionBlock block, WorkoutBlockExercise source)
    {
        var exercise = new WorkoutSessionExercise(
            Guid.CreateVersion7(),
            block.Id,
            block.UserId,
            source.ExerciseId,
            source.Position,
            source.Notes);

        foreach (var set in source.Sets)
        {
            exercise._sets.Add(WorkoutSessionSet.Create(exercise, set.Position, set.TargetMinReps, set.TargetMaxReps));
        }

        return exercise;
    }

    internal WorkoutSessionSet? FindSet(Guid setId) => _sets.SingleOrDefault(set => set.Id == setId);
}
