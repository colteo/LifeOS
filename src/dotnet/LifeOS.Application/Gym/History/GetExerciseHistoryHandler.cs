using LifeOS.Application.Gym.Sessions;

namespace LifeOS.Application.Gym.History;

// One earlier completed workout that contained the exercise: its snapshot names and every recorded set
// of that exercise in it, in execution order (an exercise done in several blocks has one group per
// BlockPosition).
public sealed record ExerciseHistoryEntry(
    Guid SessionId,
    string ProgramName,
    string WorkoutName,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<PreviousSet> Sets);

// Next is null on the last page.
public sealed record ExerciseHistoryPage(Guid ExerciseId, IReadOnlyList<ExerciseHistoryEntry> Items, WorkoutHistoryCursor? Next);

// The full history of one exercise of a session, for the active workout: the user's completed
// workouts that contained it and were completed before this session started (never the session
// itself), newest first, one small page at a time (keyset paging, like the workout history). The
// exercise must belong to the stored session, so the route cannot be used to read arbitrary ids.
public sealed class GetExerciseHistoryHandler
{
    public const int DefaultPageSize = 5;
    public const int MaxPageSize = 20;

    private readonly IWorkoutSessionRepository _sessionRepository;

    public GetExerciseHistoryHandler(IWorkoutSessionRepository sessionRepository)
    {
        _sessionRepository = sessionRepository;
    }

    // Null for a missing session, another user's session, or an exercise the session does not
    // contain. Throws ArgumentOutOfRangeException when pageSize is not 1–MaxPageSize.
    public async Task<ExerciseHistoryPage?> HandleAsync(
        Guid userId,
        Guid sessionId,
        Guid exerciseId,
        WorkoutHistoryCursor? after,
        int pageSize,
        CancellationToken cancellationToken)
    {
        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException("limit", pageSize, $"The page size must be between 1 and {MaxPageSize}.");
        }

        var session = await _sessionRepository.GetAsync(userId, sessionId, cancellationToken);

        if (session is null
            || !session.Blocks.Any(block => block.Exercises.Any(exercise => exercise.ExerciseId == exerciseId)))
        {
            return null;
        }

        // One row more than the page tells whether another page exists.
        var items = await _sessionRepository.GetExerciseHistoryPageAsync(
            userId,
            exerciseId,
            session.StartedAtUtc,
            session.Id,
            after,
            pageSize + 1,
            cancellationToken);

        var ordered = items
            .Select(item => item with
            {
                Sets = item.Sets.OrderBy(set => set.BlockPosition).ThenBy(set => set.Position).ToList()
            })
            .ToList();

        if (ordered.Count <= pageSize)
        {
            return new ExerciseHistoryPage(exerciseId, ordered, null);
        }

        var page = ordered.Take(pageSize).ToList();
        var last = page[^1];

        return new ExerciseHistoryPage(exerciseId, page, new WorkoutHistoryCursor(last.CompletedAtUtc, last.SessionId));
    }
}
