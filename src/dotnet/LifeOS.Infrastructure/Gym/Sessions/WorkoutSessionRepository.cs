using LifeOS.Application.Gym.History;
using LifeOS.Application.Gym.Sessions;
using LifeOS.Domain.Gym.Sessions;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LifeOS.Infrastructure.Gym.Sessions;

internal sealed class WorkoutSessionRepository : IWorkoutSessionRepository
{
    private readonly LifeOSDbContext _dbContext;

    // The transaction holding the session's row lock between GetForUpdateAsync and SaveAsync or
    // DeleteAsync. If the request ends without either (a not-found, completed or invalid change),
    // disposing the scoped DbContext rolls it back and releases the lock.
    private IDbContextTransaction? _changeTransaction;

    public WorkoutSessionRepository(LifeOSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> TryAddAsync(WorkoutSession session, CancellationToken cancellationToken)
    {
        _dbContext.WorkoutSessions.Add(session);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception, WorkoutSessionConfiguration.InProgressIndexName))
        {
            // Nothing was committed; detach the whole snapshot so a later save does not retry it.
            foreach (var entry in _dbContext.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added).ToList())
            {
                if (entry.Entity is WorkoutSession or WorkoutSessionBlock or WorkoutSessionExercise or WorkoutSessionSet)
                {
                    entry.State = EntityState.Detached;
                }
            }

            return false;
        }
    }

    public async Task<WorkoutSession?> GetAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        return await WholeSessions(userId)
            .Where(session => session.Id == sessionId)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<WorkoutSession?> GetInProgressAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await WholeSessions(userId)
            .Where(session => session.Status == WorkoutSessionStatus.InProgress)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
    }

    // Locks the session row (FOR UPDATE) in a transaction, then loads the whole tracked session, so
    // concurrent changes (e.g. finish while a set is recorded) run one after another.
    public async Task<WorkoutSession?> GetForUpdateAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.CurrentTransaction is null)
        {
            _changeTransaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        }

        await _dbContext.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM workout_sessions WHERE id = {sessionId} AND user_id = {userId} FOR UPDATE",
            cancellationToken);

        return await WholeSessions(userId)
            .Where(session => session.Id == sessionId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task SaveAsync(WorkoutSession session, CancellationToken cancellationToken)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(cancellationToken);
    }

    // The tracked aggregate is removed; PostgreSQL cascades through its own snapshot only.
    public async Task DeleteAsync(WorkoutSession session, CancellationToken cancellationToken)
    {
        _dbContext.WorkoutSessions.Remove(session);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(cancellationToken);
    }

    // A no-tracking projection of counts; no aggregate is materialized. Keyset paging on
    // (completed_at_utc, id), both descending.
    public async Task<IReadOnlyList<WorkoutHistoryItem>> GetCompletedPageAsync(
        Guid userId,
        WorkoutHistoryCursor? after,
        int take,
        CancellationToken cancellationToken)
    {
        var completed = _dbContext.WorkoutSessions
            .AsNoTracking()
            .Where(session => session.UserId == userId && session.Status == WorkoutSessionStatus.Completed);

        if (after is not null)
        {
            var completedAt = after.CompletedAtUtc.ToUniversalTime();
            var id = after.Id;

            completed = completed.Where(session =>
                session.CompletedAtUtc < completedAt
                || (session.CompletedAtUtc == completedAt && session.Id.CompareTo(id) < 0));
        }

        return await completed
            .OrderByDescending(session => session.CompletedAtUtc)
            .ThenByDescending(session => session.Id)
            .Take(take)
            .Select(session => new WorkoutHistoryItem(
                session.Id,
                session.ProgramName,
                session.WorkoutName,
                session.StartedAtUtc,
                session.CompletedAtUtc!.Value,
                session.Blocks.SelectMany(block => block.Exercises).SelectMany(exercise => exercise.Sets).Count(set => set.CompletedAtUtc != null),
                session.Blocks.SelectMany(block => block.Exercises).SelectMany(exercise => exercise.Sets).Count(),
                session.Blocks.SelectMany(block => block.Exercises).Count()))
            .ToListAsync(cancellationToken);
    }

    // Two queries whatever the number of exercises: the winning session per exercise (a window over the
    // user's completed sessions containing it), then the recorded sets of those exercises in those
    // sessions.
    public async Task<IReadOnlyList<PreviousExercisePerformance>> GetPreviousPerformancesAsync(
        Guid userId,
        IReadOnlyCollection<Guid> exerciseIds,
        DateTimeOffset completedBefore,
        Guid excludingSessionId,
        CancellationToken cancellationToken)
    {
        if (exerciseIds.Count == 0)
        {
            return [];
        }

        var ids = exerciseIds.ToList();
        var before = completedBefore.ToUniversalTime();

        var latest = await (
                from exercise in _dbContext.Set<WorkoutSessionExercise>()
                join block in _dbContext.Set<WorkoutSessionBlock>() on exercise.WorkoutSessionBlockId equals block.Id
                join session in _dbContext.WorkoutSessions on block.WorkoutSessionId equals session.Id
                where session.UserId == userId
                    && exercise.UserId == userId
                    && session.Status == WorkoutSessionStatus.Completed
                    && session.CompletedAtUtc < before
                    && session.Id != excludingSessionId
                    && ids.Contains(exercise.ExerciseId)
                select new { exercise.ExerciseId, SessionId = session.Id, session.WorkoutName, CompletedAtUtc = session.CompletedAtUtc!.Value })
            .GroupBy(candidate => candidate.ExerciseId)
            .Select(candidates => candidates
                .OrderByDescending(candidate => candidate.CompletedAtUtc)
                .ThenByDescending(candidate => candidate.SessionId)
                .First())
            .ToListAsync(cancellationToken);

        if (latest.Count == 0)
        {
            return [];
        }

        var sessionIds = latest.Select(winner => winner.SessionId).Distinct().ToList();

        var sets = await (
                from set in _dbContext.Set<WorkoutSessionSet>()
                join exercise in _dbContext.Set<WorkoutSessionExercise>() on set.WorkoutSessionExerciseId equals exercise.Id
                join block in _dbContext.Set<WorkoutSessionBlock>() on exercise.WorkoutSessionBlockId equals block.Id
                where block.UserId == userId
                    && sessionIds.Contains(block.WorkoutSessionId)
                    && ids.Contains(exercise.ExerciseId)
                    && set.CompletedAtUtc != null
                select new
                {
                    block.WorkoutSessionId,
                    exercise.ExerciseId,
                    BlockPosition = block.Position,
                    set.Position,
                    ActualReps = set.ActualReps!.Value,
                    set.WeightKg
                })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return latest
            .Select(winner => new PreviousExercisePerformance(
                winner.ExerciseId,
                winner.SessionId,
                winner.WorkoutName,
                winner.CompletedAtUtc,
                sets
                    .Where(set => set.WorkoutSessionId == winner.SessionId && set.ExerciseId == winner.ExerciseId)
                    .OrderBy(set => set.BlockPosition)
                    .ThenBy(set => set.Position)
                    .Select(set => new PreviousSet(set.BlockPosition, set.Position, set.ActualReps, set.WeightKg))
                    .ToList()))
            .ToList();
    }

    // Two queries per page whatever its size: the page of sessions containing the exercise (keyset on
    // (completed_at_utc, id), both descending), then the recorded sets of that exercise in them.
    public async Task<IReadOnlyList<ExerciseHistoryEntry>> GetExerciseHistoryPageAsync(
        Guid userId,
        Guid exerciseId,
        DateTimeOffset completedBefore,
        Guid excludingSessionId,
        WorkoutHistoryCursor? after,
        int take,
        CancellationToken cancellationToken)
    {
        var before = completedBefore.ToUniversalTime();

        var sessions = _dbContext.WorkoutSessions
            .AsNoTracking()
            .Where(session => session.UserId == userId
                && session.Status == WorkoutSessionStatus.Completed
                && session.CompletedAtUtc < before
                && session.Id != excludingSessionId
                && session.Blocks.Any(block => block.Exercises.Any(exercise => exercise.ExerciseId == exerciseId)));

        if (after is not null)
        {
            var completedAt = after.CompletedAtUtc.ToUniversalTime();
            var id = after.Id;

            sessions = sessions.Where(session =>
                session.CompletedAtUtc < completedAt
                || (session.CompletedAtUtc == completedAt && session.Id.CompareTo(id) < 0));
        }

        var page = await sessions
            .OrderByDescending(session => session.CompletedAtUtc)
            .ThenByDescending(session => session.Id)
            .Take(take)
            .Select(session => new { session.Id, session.ProgramName, session.WorkoutName, CompletedAtUtc = session.CompletedAtUtc!.Value })
            .ToListAsync(cancellationToken);

        if (page.Count == 0)
        {
            return [];
        }

        var sessionIds = page.Select(session => session.Id).ToList();

        var sets = await (
                from set in _dbContext.Set<WorkoutSessionSet>()
                join exercise in _dbContext.Set<WorkoutSessionExercise>() on set.WorkoutSessionExerciseId equals exercise.Id
                join block in _dbContext.Set<WorkoutSessionBlock>() on exercise.WorkoutSessionBlockId equals block.Id
                where block.UserId == userId
                    && sessionIds.Contains(block.WorkoutSessionId)
                    && exercise.ExerciseId == exerciseId
                    && set.CompletedAtUtc != null
                select new
                {
                    block.WorkoutSessionId,
                    BlockPosition = block.Position,
                    set.Position,
                    ActualReps = set.ActualReps!.Value,
                    set.WeightKg
                })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return page
            .Select(session => new ExerciseHistoryEntry(
                session.Id,
                session.ProgramName,
                session.WorkoutName,
                session.CompletedAtUtc,
                sets
                    .Where(set => set.WorkoutSessionId == session.Id)
                    .OrderBy(set => set.BlockPosition)
                    .ThenBy(set => set.Position)
                    .Select(set => new PreviousSet(set.BlockPosition, set.Position, set.ActualReps, set.WeightKg))
                    .ToList()))
            .ToList();
    }

    private async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_changeTransaction is { } transaction)
        {
            _changeTransaction = null;
            await transaction.CommitAsync(cancellationToken);
            await transaction.DisposeAsync();
        }
    }

    // The domain orders every level by position, so the query does not need to.
    private IQueryable<WorkoutSession> WholeSessions(Guid userId) =>
        _dbContext.WorkoutSessions
            .Where(session => session.UserId == userId)
            .Include(session => session.Blocks)
                .ThenInclude(block => block.Exercises)
                    .ThenInclude(exercise => exercise.Sets)
            .AsSplitQuery();
}
