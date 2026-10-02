using LifeOS.Application.Gym.Exercises;
using LifeOS.Application.Gym.Programs;
using LifeOS.Domain.Gym.Sessions;

namespace LifeOS.Application.Gym.Sessions;

public enum StartWorkoutSessionStatus
{
    Started,

    // Missing program or workout, including another user's.
    WorkoutNotFound,

    // The user already has an InProgress workout (InProgressSessionId); it must be finished or
    // discarded first.
    AnotherInProgress
}

public sealed record StartWorkoutSessionResult(
    StartWorkoutSessionStatus Status,
    WorkoutSessionDetails? Session,
    Guid? InProgressSessionId);

public enum WorkoutSessionChangeStatus
{
    Changed,

    // Missing, or another user's session.
    SessionNotFound,

    // The session has no such set.
    SetNotFound,

    // The session is completed and can no longer change.
    AlreadyCompleted
}

public sealed record WorkoutSessionChangeResult(WorkoutSessionChangeStatus Status, WorkoutSessionDetails? Session)
{
    public static WorkoutSessionChangeResult Failed(WorkoutSessionChangeStatus status) => new(status, null);
}

// Starts a workout: snapshots the owner's workout prescription into a new InProgress session with
// the server's start time. Throws ArgumentException when the workout has no exercises.
public sealed class StartWorkoutSessionHandler
{
    private readonly IWorkoutProgramRepository _programRepository;
    private readonly IWorkoutSessionRepository _sessionRepository;
    private readonly IExerciseRepository _exerciseRepository;
    private readonly TimeProvider _timeProvider;

    public StartWorkoutSessionHandler(
        IWorkoutProgramRepository programRepository,
        IWorkoutSessionRepository sessionRepository,
        IExerciseRepository exerciseRepository,
        TimeProvider timeProvider)
    {
        _programRepository = programRepository;
        _sessionRepository = sessionRepository;
        _exerciseRepository = exerciseRepository;
        _timeProvider = timeProvider;
    }

    public async Task<StartWorkoutSessionResult> HandleAsync(
        Guid userId,
        Guid programId,
        Guid workoutId,
        CancellationToken cancellationToken)
    {
        if (await _sessionRepository.GetInProgressAsync(userId, cancellationToken) is { } current)
        {
            return new StartWorkoutSessionResult(StartWorkoutSessionStatus.AnotherInProgress, null, current.Id);
        }

        var program = await _programRepository.GetAsync(userId, programId, cancellationToken);

        if (program?.FindWorkout(workoutId) is not { } workout)
        {
            return new StartWorkoutSessionResult(StartWorkoutSessionStatus.WorkoutNotFound, null, null);
        }

        var session = WorkoutSession.Start(program, workout, _timeProvider.GetUtcNow());

        if (!await _sessionRepository.TryAddAsync(session, cancellationToken))
        {
            // Lost a race with another start: report the winner so the client can resume it.
            var winner = await _sessionRepository.GetInProgressAsync(userId, cancellationToken);

            return new StartWorkoutSessionResult(StartWorkoutSessionStatus.AnotherInProgress, null, winner?.Id);
        }

        return new StartWorkoutSessionResult(
            StartWorkoutSessionStatus.Started,
            await WorkoutSessionDetails.ReadAsync(session, _exerciseRepository, _timeProvider, cancellationToken),
            null);
    }
}

public sealed class GetWorkoutSessionHandler
{
    private readonly IWorkoutSessionRepository _sessionRepository;
    private readonly IExerciseRepository _exerciseRepository;
    private readonly TimeProvider _timeProvider;

    public GetWorkoutSessionHandler(
        IWorkoutSessionRepository sessionRepository,
        IExerciseRepository exerciseRepository,
        TimeProvider timeProvider)
    {
        _sessionRepository = sessionRepository;
        _exerciseRepository = exerciseRepository;
        _timeProvider = timeProvider;
    }

    // Null for a missing session or another user's session.
    public async Task<WorkoutSessionDetails?> HandleAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
        await _sessionRepository.GetAsync(userId, sessionId, cancellationToken) is { } session
            ? await WorkoutSessionDetails.ReadAsync(session, _exerciseRepository, _timeProvider, cancellationToken)
            : null;

    // The user's InProgress session, or null when none.
    public async Task<WorkoutSessionDetails?> HandleCurrentAsync(Guid userId, CancellationToken cancellationToken) =>
        await _sessionRepository.GetInProgressAsync(userId, cancellationToken) is { } session
            ? await WorkoutSessionDetails.ReadAsync(session, _exerciseRepository, _timeProvider, cancellationToken)
            : null;
}

// Records a set (completes it) or corrects an already recorded one while the session is in progress.
// Throws ArgumentException for invalid reps or weight.
public sealed class RecordWorkoutSetHandler
{
    private readonly IWorkoutSessionRepository _sessionRepository;
    private readonly IExerciseRepository _exerciseRepository;
    private readonly TimeProvider _timeProvider;

    public RecordWorkoutSetHandler(
        IWorkoutSessionRepository sessionRepository,
        IExerciseRepository exerciseRepository,
        TimeProvider timeProvider)
    {
        _sessionRepository = sessionRepository;
        _exerciseRepository = exerciseRepository;
        _timeProvider = timeProvider;
    }

    public Task<WorkoutSessionChangeResult> HandleAsync(
        Guid userId,
        Guid sessionId,
        Guid setId,
        int actualReps,
        decimal? weightKg,
        CancellationToken cancellationToken) =>
        WorkoutSessionChange.RunAsync(
            _sessionRepository,
            _exerciseRepository,
            _timeProvider,
            userId,
            sessionId,
            session =>
            {
                if (!session.HasSet(setId))
                {
                    return WorkoutSessionChangeStatus.SetNotFound;
                }

                session.RecordSet(setId, actualReps, weightKg, _timeProvider.GetUtcNow());

                return WorkoutSessionChangeStatus.Changed;
            },
            cancellationToken);
}

// Finishes the workout at the server's time; pending sets stay pending.
public sealed class FinishWorkoutSessionHandler
{
    private readonly IWorkoutSessionRepository _sessionRepository;
    private readonly IExerciseRepository _exerciseRepository;
    private readonly TimeProvider _timeProvider;

    public FinishWorkoutSessionHandler(
        IWorkoutSessionRepository sessionRepository,
        IExerciseRepository exerciseRepository,
        TimeProvider timeProvider)
    {
        _sessionRepository = sessionRepository;
        _exerciseRepository = exerciseRepository;
        _timeProvider = timeProvider;
    }

    public Task<WorkoutSessionChangeResult> HandleAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
        WorkoutSessionChange.RunAsync(
            _sessionRepository,
            _exerciseRepository,
            _timeProvider,
            userId,
            sessionId,
            session =>
            {
                session.Finish(_timeProvider.GetUtcNow());

                return WorkoutSessionChangeStatus.Changed;
            },
            cancellationToken);
}

// Deletes an InProgress session with its snapshot. Completed sessions are kept.
public sealed class DiscardWorkoutSessionHandler
{
    private readonly IWorkoutSessionRepository _sessionRepository;

    public DiscardWorkoutSessionHandler(IWorkoutSessionRepository sessionRepository)
    {
        _sessionRepository = sessionRepository;
    }

    // Changed when discarded.
    public async Task<WorkoutSessionChangeStatus> HandleAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessionRepository.GetForUpdateAsync(userId, sessionId, cancellationToken);

        if (session is null)
        {
            return WorkoutSessionChangeStatus.SessionNotFound;
        }

        if (session.Status != WorkoutSessionStatus.InProgress)
        {
            return WorkoutSessionChangeStatus.AlreadyCompleted;
        }

        session.EnsureCanDiscard();
        await _sessionRepository.DeleteAsync(session, cancellationToken);

        return WorkoutSessionChangeStatus.Changed;
    }
}

// The shared shape of a change to an InProgress session: load it for update, refuse a completed one,
// apply the change through the domain, save, and return the whole session. A change that reports a
// failure (or throws ArgumentException for invalid input) saves nothing.
internal static class WorkoutSessionChange
{
    public static async Task<WorkoutSessionChangeResult> RunAsync(
        IWorkoutSessionRepository sessionRepository,
        IExerciseRepository exerciseRepository,
        TimeProvider timeProvider,
        Guid userId,
        Guid sessionId,
        Func<WorkoutSession, WorkoutSessionChangeStatus> change,
        CancellationToken cancellationToken)
    {
        var session = await sessionRepository.GetForUpdateAsync(userId, sessionId, cancellationToken);

        if (session is null)
        {
            return WorkoutSessionChangeResult.Failed(WorkoutSessionChangeStatus.SessionNotFound);
        }

        if (session.Status != WorkoutSessionStatus.InProgress)
        {
            return WorkoutSessionChangeResult.Failed(WorkoutSessionChangeStatus.AlreadyCompleted);
        }

        var status = change(session);

        if (status != WorkoutSessionChangeStatus.Changed)
        {
            return WorkoutSessionChangeResult.Failed(status);
        }

        await sessionRepository.SaveAsync(session, cancellationToken);

        return new WorkoutSessionChangeResult(
            WorkoutSessionChangeStatus.Changed,
            await WorkoutSessionDetails.ReadAsync(session, exerciseRepository, timeProvider, cancellationToken));
    }
}
