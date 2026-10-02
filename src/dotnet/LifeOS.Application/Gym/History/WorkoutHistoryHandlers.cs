using LifeOS.Application.Gym.Exercises;
using LifeOS.Application.Gym.Sessions;
using LifeOS.Domain.Gym.Sessions;

namespace LifeOS.Application.Gym.History;

// One completed workout in the history list, from its snapshot only (names as they were at start).
public sealed record WorkoutHistoryItem(
    Guid Id,
    string ProgramName,
    string WorkoutName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int CompletedSetCount,
    int PrescribedSetCount,
    int ExerciseCount);

// The position after which the next page starts: history is ordered by CompletedAtUtc descending,
// then Id descending, so a cursor is the last item's pair.
public sealed record WorkoutHistoryCursor(DateTimeOffset CompletedAtUtc, Guid Id);

// Next is null on the last page.
public sealed record WorkoutHistoryPage(IReadOnlyList<WorkoutHistoryItem> Items, WorkoutHistoryCursor? Next);

// The user's completed workouts, newest first, one bounded page at a time (keyset paging, so workouts
// finished while paging never shift or repeat rows). InProgress workouts are not history.
public sealed class GetWorkoutHistoryHandler
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    private readonly IWorkoutSessionRepository _sessionRepository;

    public GetWorkoutHistoryHandler(IWorkoutSessionRepository sessionRepository)
    {
        _sessionRepository = sessionRepository;
    }

    // Throws ArgumentOutOfRangeException when pageSize is not 1–MaxPageSize.
    public async Task<WorkoutHistoryPage> HandleAsync(
        Guid userId,
        WorkoutHistoryCursor? after,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException("limit", pageSize, $"The page size must be between 1 and {MaxPageSize}.");
        }

        // One row more than the page tells whether another page exists.
        var items = await _sessionRepository.GetCompletedPageAsync(userId, after, pageSize + 1, cancellationToken);

        if (items.Count <= pageSize)
        {
            return new WorkoutHistoryPage(items, null);
        }

        var page = items.Take(pageSize).ToList();
        var last = page[^1];

        return new WorkoutHistoryPage(page, new WorkoutHistoryCursor(last.CompletedAtUtc, last.Id));
    }
}

// One completed workout, read-only, exactly as its snapshot recorded it.
public sealed class GetWorkoutHistoryDetailHandler
{
    private readonly IWorkoutSessionRepository _sessionRepository;
    private readonly IExerciseRepository _exerciseRepository;
    private readonly TimeProvider _timeProvider;

    public GetWorkoutHistoryDetailHandler(
        IWorkoutSessionRepository sessionRepository,
        IExerciseRepository exerciseRepository,
        TimeProvider timeProvider)
    {
        _sessionRepository = sessionRepository;
        _exerciseRepository = exerciseRepository;
        _timeProvider = timeProvider;
    }

    // Null for a missing session, another user's session, or one still in progress (not history yet).
    public async Task<WorkoutSessionDetails?> HandleAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken) =>
        await _sessionRepository.GetAsync(userId, sessionId, cancellationToken) is { Status: WorkoutSessionStatus.Completed } session
            ? await WorkoutSessionDetails.ReadAsync(session, _exerciseRepository, _timeProvider, cancellationToken)
            : null;
}
