using LifeOS.Application.Gym.Programs;
using LifeOS.Application.Gym.Programs.GetWorkoutPrograms;
using LifeOS.Domain.Gym.Programs;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LifeOS.Infrastructure.Gym.Programs;

internal sealed class WorkoutProgramRepository : IWorkoutProgramRepository
{
    private readonly LifeOSDbContext _dbContext;

    // The transaction holding the program's row lock between GetForUpdateAsync and SaveAsync. If the
    // request ends without SaveAsync (a not-found or invalid edit), disposing the scoped DbContext
    // rolls it back and releases the lock.
    private IDbContextTransaction? _editTransaction;

    public WorkoutProgramRepository(LifeOSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(WorkoutProgram program, CancellationToken cancellationToken)
    {
        _dbContext.WorkoutPrograms.Add(program);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkoutProgramSummary>> GetSummariesAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.WorkoutPrograms
            .AsNoTracking()
            .Where(program => program.UserId == userId)
            .Select(program => new WorkoutProgramSummary(
                program.Id,
                program.Name,
                program.Workouts.Count(),
                program.CreatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<WorkoutProgram?> GetAsync(Guid userId, Guid programId, CancellationToken cancellationToken)
    {
        return await WholeProgram(userId, programId)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
    }

    // Locks the program row (FOR UPDATE) in a transaction, then loads the whole tracked program.
    // Every edit of the program takes this lock first, so concurrent edits run one after another
    // and each sees the previous one's result (e.g. two appended workouts get positions 1 and 2).
    public async Task<WorkoutProgram?> GetForUpdateAsync(Guid userId, Guid programId, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.CurrentTransaction is null)
        {
            _editTransaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        }

        await _dbContext.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM workout_programs WHERE id = {programId} AND user_id = {userId} FOR UPDATE",
            cancellationToken);

        return await WholeProgram(userId, programId).SingleOrDefaultAsync(cancellationToken);
    }

    // EF Core tracks the aggregate: new workouts, blocks and sets are inserted, removed ones deleted
    // (orphans of required relationships), and changed positions, names and reps updated. The
    // deferred sibling-position constraints are checked at commit.
    public async Task SaveAsync(WorkoutProgram program, CancellationToken cancellationToken)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (_editTransaction is { } transaction)
        {
            _editTransaction = null;
            await transaction.CommitAsync(cancellationToken);
            await transaction.DisposeAsync();
        }
    }

    // One statement on the program row; PostgreSQL cascades to its own workouts, blocks, block
    // exercises and sets only. Exercises are referenced with RESTRICT and are never deleted here.
    public async Task<bool> DeleteAsync(Guid userId, Guid programId, CancellationToken cancellationToken)
    {
        var deleted = await _dbContext.WorkoutPrograms
            .Where(program => program.Id == programId && program.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted == 1;
    }

    // The domain orders every level by position, so the query does not need to.
    private IQueryable<WorkoutProgram> WholeProgram(Guid userId, Guid programId) =>
        _dbContext.WorkoutPrograms
            .Where(program => program.Id == programId && program.UserId == userId)
            .Include(program => program.Workouts)
                .ThenInclude(workout => workout.Blocks)
                    .ThenInclude(block => block.Exercises)
                        .ThenInclude(exercise => exercise.Sets)
            .AsSplitQuery();
}
