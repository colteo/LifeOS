using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;

namespace LifeOS.Domain.Gym.Training;

// A workout program being trained: the template plus its own progress. The user repeats the
// program's workouts for TotalCycles cycles. Within a cycle every workout must be done once, in any
// order and on any day; there is no schedule. When the last workout of a cycle is done the next
// cycle opens; after the last cycle the program is Completed.
//
// The workouts of a cycle are the template's CURRENT workouts: a workout added to the program while
// it is active is required from then on, a deleted one no longer is. Progress (cycles and which
// workout counted in which cycle) belongs to this aggregate only; the template is never changed.
// Timestamps come from the server clock through the Application layer.
public sealed class ActiveProgram
{
    public const int MaxCycles = 99;

    private readonly List<ActiveProgramCompletion> _completions = [];

    private ActiveProgram(Guid id, Guid userId, Guid workoutProgramId, int totalCycles, DateTimeOffset activatedAtUtc)
    {
        Id = id;
        UserId = userId;
        WorkoutProgramId = workoutProgramId;
        TotalCycles = totalCycles;
        CurrentCycle = 1;
        Status = ActiveProgramStatus.Active;
        ActivatedAtUtc = activatedAtUtc;
    }

    public Guid Id { get; }

    // The owning LifeOS user.
    public Guid UserId { get; }

    // The template being trained.
    public Guid WorkoutProgramId { get; }

    public int TotalCycles { get; }

    // 1-based; equals TotalCycles once Completed.
    public int CurrentCycle { get; private set; }

    public ActiveProgramStatus Status { get; private set; }

    public DateTimeOffset ActivatedAtUtc { get; }

    // When it was completed or stopped; null while active.
    public DateTimeOffset? EndedAtUtc { get; private set; }

    public IReadOnlyList<ActiveProgramCompletion> Completions =>
        _completions.OrderBy(completion => completion.Cycle).ThenBy(completion => completion.CompletedAtUtc).ToList();

    // The completions of the current cycle, by workout. Empty once the program has ended.
    public IReadOnlyList<ActiveProgramCompletion> CurrentCycleCompletions =>
        Status == ActiveProgramStatus.Active
            ? Completions.Where(completion => completion.Cycle == CurrentCycle).ToList()
            : [];

    // Every workout must be startable (have exercises), otherwise a cycle could never be completed.
    public static ActiveProgram Activate(WorkoutProgram program, int totalCycles, DateTimeOffset activatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(program);

        if (totalCycles is < 1 or > MaxCycles)
        {
            throw new ArgumentOutOfRangeException(nameof(totalCycles), totalCycles, $"Cycles must be between 1 and {MaxCycles}.");
        }

        if (program.Workouts.Count == 0)
        {
            throw new ArgumentException("Add a workout to the program before activating it.", "programId");
        }

        if (program.Workouts.Any(workout => workout.Blocks.Count == 0))
        {
            throw new ArgumentException("Every workout of the program needs exercises before activating it.", "programId");
        }

        return new ActiveProgram(Guid.CreateVersion7(), program.UserId, program.Id, totalCycles, activatedAtUtc.ToUniversalTime());
    }

    public bool IsCompletedInCurrentCycle(Guid workoutId) =>
        CurrentCycleCompletions.Any(completion => completion.WorkoutTemplateId == workoutId);

    // Counts a finished session of this program for the current cycle, then opens the next cycle
    // (or completes the program) when every workout of the program is done in it. Returns false,
    // changing nothing, when the session does not count: the program is no longer active, the session
    // is not finished, it was started from another program or from a workout no longer in it, or
    // its workout was already done in this cycle (a repeat is ordinary training, not progress).
    public bool RecordWorkout(WorkoutProgram program, WorkoutSession session, DateTimeOffset nowUtc)
    {
        EnsureTemplate(program);
        ArgumentNullException.ThrowIfNull(session);

        if (Status != ActiveProgramStatus.Active
            || session.Status != WorkoutSessionStatus.Completed
            || session.UserId != UserId
            || session.WorkoutProgramId != WorkoutProgramId
            || session.WorkoutTemplateId is not { } workoutId
            || program.FindWorkout(workoutId) is null
            || IsCompletedInCurrentCycle(workoutId)
            || _completions.Any(completion => completion.WorkoutSessionId == session.Id))
        {
            return false;
        }

        _completions.Add(ActiveProgramCompletion.Create(this, workoutId, session.Id, session.CompletedAtUtc!.Value));
        AdvanceIfCycleDone(program, nowUtc);

        return true;
    }

    // Opens the next cycle, or completes the program after the last one, when every current workout
    // of the program is done in the current cycle. Needed after a workout is removed from the program:
    // the remaining ones may all be done already. A program without workouts never advances. Returns
    // true when it advanced.
    public bool AdvanceIfCycleDone(WorkoutProgram program, DateTimeOffset nowUtc)
    {
        EnsureTemplate(program);

        if (Status != ActiveProgramStatus.Active
            || program.Workouts.Count == 0
            || !program.Workouts.All(workout => IsCompletedInCurrentCycle(workout.Id)))
        {
            return false;
        }

        if (CurrentCycle == TotalCycles)
        {
            Status = ActiveProgramStatus.Completed;
            EndedAtUtc = nowUtc.ToUniversalTime();
        }
        else
        {
            CurrentCycle++;
        }

        return true;
    }

    // Ends the program early; its progress is kept.
    public void Stop(DateTimeOffset nowUtc)
    {
        if (Status != ActiveProgramStatus.Active)
        {
            throw new InvalidOperationException("This program is no longer active.");
        }

        Status = ActiveProgramStatus.Stopped;
        EndedAtUtc = nowUtc.ToUniversalTime();
    }

    private void EnsureTemplate(WorkoutProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        if (program.Id != WorkoutProgramId || program.UserId != UserId)
        {
            throw new ArgumentException("The program must be the active program's template.", nameof(program));
        }
    }
}
