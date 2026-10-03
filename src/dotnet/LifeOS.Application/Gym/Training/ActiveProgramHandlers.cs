using LifeOS.Application.Gym.Programs;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;
using LifeOS.Domain.Gym.Training;

namespace LifeOS.Application.Gym.Training;

// The active program as Train shows it: the current cycle's workouts still to do (by position) and
// those already done in it (in the order they were done).
public sealed record ActiveProgramDetails(
    Guid Id,
    Guid ProgramId,
    string ProgramName,
    int TotalCycles,
    int CurrentCycle,
    DateTimeOffset ActivatedAtUtc,
    IReadOnlyList<TrainingWorkout> ToDo,
    IReadOnlyList<CompletedCycleWorkout> Done)
{
    internal static ActiveProgramDetails Read(ActiveProgram active, TrainingProgram template)
    {
        var done = active.CurrentCycleCompletions.ToDictionary(completion => completion.WorkoutTemplateId);
        var workouts = template.Workouts.OrderBy(workout => workout.Position).ToList();

        return new ActiveProgramDetails(
            active.Id,
            active.WorkoutProgramId,
            template.Name,
            active.TotalCycles,
            active.CurrentCycle,
            active.ActivatedAtUtc,
            workouts.Where(workout => !done.ContainsKey(workout.Id)).ToList(),
            workouts
                .Where(workout => done.ContainsKey(workout.Id))
                .Select(workout => new CompletedCycleWorkout(
                    workout.Id,
                    workout.Name,
                    workout.Position,
                    done[workout.Id].WorkoutSessionId,
                    done[workout.Id].CompletedAtUtc))
                .OrderBy(workout => workout.CompletedAtUtc)
                .ThenBy(workout => workout.Position)
                .ToList());
    }
}

public sealed record CompletedCycleWorkout(Guid Id, string Name, int Position, Guid SessionId, DateTimeOffset CompletedAtUtc);

// Keeps the active program's progress in step with training and authoring. Both methods run inside
// the change of the other aggregate (a finishing session, a program losing a workout) and store the
// active program in that change's transaction, so the two are saved together.
public sealed class ActiveProgramProgress
{
    private readonly IActiveProgramRepository _activeRepository;
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly TimeProvider _timeProvider;

    public ActiveProgramProgress(
        IActiveProgramRepository activeRepository,
        IWorkoutProgramRepository programRepository,
        TimeProvider timeProvider)
    {
        _activeRepository = activeRepository;
        _programRepository = programRepository;
        _timeProvider = timeProvider;
    }

    // A session just finished: it counts for the current cycle when it was started from a workout of
    // the active program not yet done in that cycle.
    public async Task RecordFinishedAsync(WorkoutSession session, CancellationToken cancellationToken)
    {
        if (session.WorkoutProgramId is not { } programId)
        {
            return;
        }

        var active = await _activeRepository.GetActiveForUpdateAsync(session.UserId, cancellationToken);

        if (active is null || active.WorkoutProgramId != programId
            || await _programRepository.GetAsync(session.UserId, programId, cancellationToken) is not { } program)
        {
            return;
        }

        if (active.RecordWorkout(program, session, _timeProvider.GetUtcNow()))
        {
            await _activeRepository.SaveAsync(active, cancellationToken);
        }
    }

    // A workout was removed from the program: the remaining ones may all be done in the current
    // cycle, which then advances.
    public async Task ReconcileAsync(WorkoutProgram program, CancellationToken cancellationToken)
    {
        var active = await _activeRepository.GetActiveForUpdateAsync(program.UserId, cancellationToken);

        if (active is null || active.WorkoutProgramId != program.Id)
        {
            return;
        }

        if (active.AdvanceIfCycleDone(program, _timeProvider.GetUtcNow()))
        {
            await _activeRepository.SaveAsync(active, cancellationToken);
        }
    }
}

// The user's active program for Train; null when none is active.
public sealed class GetActiveProgramHandler
{
    private readonly IActiveProgramRepository _activeRepository;
    private readonly IWorkoutProgramRepository _programRepository;

    public GetActiveProgramHandler(IActiveProgramRepository activeRepository, IWorkoutProgramRepository programRepository)
    {
        _activeRepository = activeRepository;
        _programRepository = programRepository;
    }

    public async Task<ActiveProgramDetails?> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (await _activeRepository.GetActiveAsync(userId, cancellationToken) is not { } active)
        {
            return null;
        }

        // Deleting the template deletes its active program, so it exists; a concurrent delete reads as none.
        var template = await _programRepository.GetTrainingProgramAsync(userId, active.WorkoutProgramId, cancellationToken);

        return template is null ? null : ActiveProgramDetails.Read(active, template);
    }
}

public enum ActivateProgramStatus
{
    Activated,

    // Missing, or another user's program.
    ProgramNotFound,

    // The user already has an active program (ActiveProgramId); it must be stopped first.
    AnotherActive
}

public sealed record ActivateProgramResult(ActivateProgramStatus Status, ActiveProgramDetails? Program, Guid? ActiveProgramId);

// Starts training a program for a number of cycles, from cycle 1. Throws ArgumentException for an
// invalid number of cycles or a program that cannot be completed (no workouts, or a workout without
// exercises).
public sealed class ActivateProgramHandler
{
    private readonly IActiveProgramRepository _activeRepository;
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly TimeProvider _timeProvider;

    public ActivateProgramHandler(
        IActiveProgramRepository activeRepository,
        IWorkoutProgramRepository programRepository,
        TimeProvider timeProvider)
    {
        _activeRepository = activeRepository;
        _programRepository = programRepository;
        _timeProvider = timeProvider;
    }

    public async Task<ActivateProgramResult> HandleAsync(Guid userId, Guid programId, int cycles, CancellationToken cancellationToken)
    {
        if (await _activeRepository.GetActiveAsync(userId, cancellationToken) is { } current)
        {
            return new ActivateProgramResult(ActivateProgramStatus.AnotherActive, null, current.Id);
        }

        if (await _programRepository.GetAsync(userId, programId, cancellationToken) is not { } program)
        {
            return new ActivateProgramResult(ActivateProgramStatus.ProgramNotFound, null, null);
        }

        var active = ActiveProgram.Activate(program, cycles, _timeProvider.GetUtcNow());

        if (!await _activeRepository.TryAddAsync(active, cancellationToken))
        {
            // Lost a race with another activation.
            var winner = await _activeRepository.GetActiveAsync(userId, cancellationToken);

            return new ActivateProgramResult(ActivateProgramStatus.AnotherActive, null, winner?.Id);
        }

        var template = await _programRepository.GetTrainingProgramAsync(userId, programId, cancellationToken);

        return template is null
            ? new ActivateProgramResult(ActivateProgramStatus.ProgramNotFound, null, null)
            : new ActivateProgramResult(ActivateProgramStatus.Activated, ActiveProgramDetails.Read(active, template), null);
    }
}

// Ends the active program early. Completed workouts stay in history; the template is unchanged.
public sealed class StopActiveProgramHandler
{
    private readonly IActiveProgramRepository _activeRepository;
    private readonly TimeProvider _timeProvider;

    public StopActiveProgramHandler(IActiveProgramRepository activeRepository, TimeProvider timeProvider)
    {
        _activeRepository = activeRepository;
        _timeProvider = timeProvider;
    }

    // False when no program is active.
    public async Task<bool> HandleAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (await _activeRepository.GetActiveForUpdateAsync(userId, cancellationToken) is not { } active)
        {
            return false;
        }

        active.Stop(_timeProvider.GetUtcNow());
        await _activeRepository.SaveAsync(active, cancellationToken);

        return true;
    }
}
