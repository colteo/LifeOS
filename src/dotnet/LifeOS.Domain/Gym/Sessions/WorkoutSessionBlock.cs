using LifeOS.Domain.Gym.Programs;

namespace LifeOS.Domain.Gym.Sessions;

// One block of the snapshot: its order, kind and rest as prescribed when the workout started.
// In a superset the sets with the same position form a round (A then B) and the rest follows the
// round. With different set counts a round holds only the sets that exist: A = 4 and B = 3 give
// A1 B1, A2 B2, A3 B3, A4.
public sealed class WorkoutSessionBlock
{
    private readonly List<WorkoutSessionExercise> _exercises = [];

    private WorkoutSessionBlock(Guid id, Guid workoutSessionId, Guid userId, int position, WorkoutBlockKind kind, int? restSeconds)
    {
        Id = id;
        WorkoutSessionId = workoutSessionId;
        UserId = userId;
        Position = position;
        Kind = kind;
        RestSeconds = restSeconds;
    }

    public Guid Id { get; }

    public Guid WorkoutSessionId { get; }

    // The session's owner; the database's composite keys use it as an ownership backstop.
    public Guid UserId { get; }

    public int Position { get; }

    public WorkoutBlockKind Kind { get; }

    // Rest after each set (Single) or each round (Superset); null when none was prescribed.
    public int? RestSeconds { get; }

    // Ordered by position: one exercise for a Single block, A (1) and B (2) for a Superset.
    public IReadOnlyList<WorkoutSessionExercise> Exercises => _exercises.OrderBy(exercise => exercise.Position).ToList();

    // The natural execution order of the block: rounds by set position, A before B within a round.
    public IReadOnlyList<WorkoutSessionSet> ExecutionOrder =>
        Exercises
            .SelectMany(exercise => exercise.Sets.Select(set => (Set: set, Slot: exercise.Position)))
            .OrderBy(item => item.Set.Position)
            .ThenBy(item => item.Slot)
            .Select(item => item.Set)
            .ToList();

    internal static WorkoutSessionBlock Snapshot(WorkoutSession session, WorkoutBlock source)
    {
        var block = new WorkoutSessionBlock(
            Guid.CreateVersion7(),
            session.Id,
            session.UserId,
            source.Position,
            source.Kind,
            source.RestSeconds);

        foreach (var exercise in source.Exercises)
        {
            block._exercises.Add(WorkoutSessionExercise.Snapshot(block, exercise));
        }

        return block;
    }

    // True when every set of that round (the same position in this block) is completed.
    internal bool IsRoundCompleted(int setPosition) =>
        _exercises
            .SelectMany(exercise => exercise.Sets)
            .Where(set => set.Position == setPosition)
            .All(set => set.IsCompleted);

    internal WorkoutSessionSet? FindSet(Guid setId) =>
        _exercises.Select(exercise => exercise.FindSet(setId)).FirstOrDefault(set => set is not null);
}
