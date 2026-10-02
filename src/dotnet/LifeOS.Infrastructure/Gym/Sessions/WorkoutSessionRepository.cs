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
