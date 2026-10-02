using LifeOS.Domain.Gym.Programs;

namespace LifeOS.Domain.Gym.Sessions;

// One execution of a workout. Starting copies the workout's prescription (blocks, kinds, rest,
// exercises by stable id, notes, ordered set targets) into the session, so later edits or deletion
// of the source program never change it: the source ids are provenance only. A session is
// InProgress until it is finished, then Completed and immutable. Timestamps come from the server
// clock through the Application layer.
public sealed class WorkoutSession
{
    private readonly List<WorkoutSessionBlock> _blocks = [];

    private WorkoutSession(
        Guid id,
        Guid userId,
        Guid? workoutProgramId,
        Guid? workoutTemplateId,
        string programName,
        string workoutName,
        DateTimeOffset startedAtUtc)
    {
        Id = id;
        UserId = userId;
        WorkoutProgramId = workoutProgramId;
        WorkoutTemplateId = workoutTemplateId;
        ProgramName = programName;
        WorkoutName = workoutName;
        StartedAtUtc = startedAtUtc;
        Status = WorkoutSessionStatus.InProgress;
    }

    public Guid Id { get; }

    // The owning LifeOS user.
    public Guid UserId { get; }

    // The program and workout it was started from; the database clears them when those are deleted.
    public Guid? WorkoutProgramId { get; }

    public Guid? WorkoutTemplateId { get; }

    // The names when the workout started.
    public string ProgramName { get; }

    public string WorkoutName { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public WorkoutSessionStatus Status { get; private set; }

    public IReadOnlyList<WorkoutSessionBlock> Blocks => _blocks.OrderBy(block => block.Position).ToList();

    // Every set in natural execution order: blocks by position, then rounds, A before B.
    public IReadOnlyList<WorkoutSessionSet> ExecutionOrder => Blocks.SelectMany(block => block.ExecutionOrder).ToList();

    public int PrescribedSetCount => ExecutionOrder.Count;

    public int CompletedSetCount => ExecutionOrder.Count(set => set.IsCompleted);

    // Null while in progress.
    public TimeSpan? Duration => CompletedAtUtc - StartedAtUtc;

    public static WorkoutSession Start(WorkoutProgram program, WorkoutTemplate workout, DateTimeOffset startedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(workout);

        if (program.FindWorkout(workout.Id) is null || workout.UserId != program.UserId)
        {
            throw new ArgumentException("The workout must belong to the program.", nameof(workout));
        }

        if (workout.Blocks.Count == 0)
        {
            throw new ArgumentException("This workout has no exercises yet.", "workoutId");
        }

        var session = new WorkoutSession(
            Guid.CreateVersion7(),
            program.UserId,
            program.Id,
            workout.Id,
            program.Name,
            workout.Name,
            startedAtUtc.ToUniversalTime());

        foreach (var block in workout.Blocks)
        {
            session._blocks.Add(WorkoutSessionBlock.Snapshot(session, block));
        }

        return session;
    }

    public bool HasSet(Guid setId) => FindSet(setId) is not null;

    // Completes a pending set or corrects a completed one. Throws InvalidOperationException once the
    // session is completed, ArgumentException for invalid reps or weight, or an unknown set.
    public WorkoutSessionSet RecordSet(Guid setId, int actualReps, decimal? weightKg, DateTimeOffset recordedAtUtc)
    {
        EnsureInProgress();

        var set = FindSet(setId) ?? throw new ArgumentException("The workout has no such set.", nameof(setId));
        set.Record(actualReps, weightKg, recordedAtUtc);

        return set;
    }

    // Finishing does not require every set: stopping early is real training behaviour.
    public void Finish(DateTimeOffset completedAtUtc)
    {
        EnsureInProgress();

        var completedAt = completedAtUtc.ToUniversalTime();
        CompletedAtUtc = completedAt < StartedAtUtc ? StartedAtUtc : completedAt;
        Status = WorkoutSessionStatus.Completed;
    }

    // Only an unfinished session may be discarded; completed history is kept.
    public void EnsureCanDiscard() => EnsureInProgress();

    // The rest started by the most recently completed set, when that completion closed its round
    // (Single: every set; Superset: once A and B of the round are both done), the block prescribes
    // rest and some set is still pending. Null otherwise, and always null once completed. Whether the
    // rest is already over is for the caller to decide against its clock (EndsAtUtc).
    public RestPeriod? CurrentRest()
    {
        if (Status != WorkoutSessionStatus.InProgress)
        {
            return null;
        }

        var sets = Blocks
            .SelectMany(block => block.ExecutionOrder.Select(set => (Block: block, Set: set)))
            .ToList();

        if (sets.All(item => item.Set.IsCompleted))
        {
            return null;
        }

        // Ties (same instant) resolve to the later set in execution order: OrderBy is stable.
        var latest = sets
            .Where(item => item.Set.IsCompleted)
            .OrderBy(item => item.Set.CompletedAtUtc)
            .LastOrDefault();

        if (latest.Set is null
            || latest.Block.RestSeconds is not { } seconds
            || !latest.Block.IsRoundCompleted(latest.Set.Position))
        {
            return null;
        }

        return new RestPeriod(latest.Set.CompletedAtUtc!.Value, seconds);
    }

    private WorkoutSessionSet? FindSet(Guid setId) =>
        _blocks.Select(block => block.FindSet(setId)).FirstOrDefault(set => set is not null);

    private void EnsureInProgress()
    {
        if (Status != WorkoutSessionStatus.InProgress)
        {
            throw new InvalidOperationException("This workout is already finished.");
        }
    }
}
