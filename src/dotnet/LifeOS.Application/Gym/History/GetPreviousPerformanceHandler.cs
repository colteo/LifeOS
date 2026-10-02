using LifeOS.Application.Gym.Sessions;

namespace LifeOS.Application.Gym.History;

// One recorded set of the previous workout. BlockPosition separates the occurrences of an exercise
// that appeared in more than one block; Position is its real set number within that block.
public sealed record PreviousSet(int BlockPosition, int Position, int ActualReps, decimal? WeightKg);

// What the user last did for one exercise: the recorded sets of that exercise in ONE previous
// completed session, in execution order (block position, then set position).
public sealed record PreviousExercisePerformance(
    Guid ExerciseId,
    Guid SessionId,
    string WorkoutName,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<PreviousSet> Sets);

public sealed record PreviousPerformance(Guid SessionId, IReadOnlyList<PreviousExercisePerformance> Exercises);

// For each exercise of one of the user's sessions, the most recent completed workout that contained it
// and was completed before this session started. Exercises without one are absent. The exercises come
// from the stored session, never from the client.
public sealed class GetPreviousPerformanceHandler
{
    private readonly IWorkoutSessionRepository _sessionRepository;

    public GetPreviousPerformanceHandler(IWorkoutSessionRepository sessionRepository)
    {
        _sessionRepository = sessionRepository;
    }

    // Null for a missing session or another user's session.
    public async Task<PreviousPerformance?> HandleAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await _sessionRepository.GetAsync(userId, sessionId, cancellationToken);

        if (session is null)
        {
            return null;
        }

        // In the session's own order, so the response follows the workout.
        var exerciseIds = session.Blocks
            .SelectMany(block => block.Exercises)
            .Select(exercise => exercise.ExerciseId)
            .Distinct()
            .ToList();

        var previous = await _sessionRepository.GetPreviousPerformancesAsync(
            userId,
            exerciseIds,
            session.StartedAtUtc,
            session.Id,
            cancellationToken);

        var byExercise = previous.ToDictionary(performance => performance.ExerciseId);

        return new PreviousPerformance(
            session.Id,
            exerciseIds
                .Where(byExercise.ContainsKey)
                .Select(exerciseId => byExercise[exerciseId] with
                {
                    Sets = byExercise[exerciseId].Sets
                        .OrderBy(set => set.BlockPosition)
                        .ThenBy(set => set.Position)
                        .ToList()
                })
                .ToList());
    }
}
