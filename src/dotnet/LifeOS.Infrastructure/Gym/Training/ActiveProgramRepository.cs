using LifeOS.Application.Gym.Training;
using LifeOS.Domain.Gym.Training;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LifeOS.Infrastructure.Gym.Training;

internal sealed class ActiveProgramRepository : IActiveProgramRepository
{
    private readonly LifeOSDbContext _dbContext;

    // The transaction holding the active program's row lock between GetActiveForUpdateAsync and
    // SaveAsync, when this repository started it. Inside another aggregate's change (a finishing
    // session, a program edit) the request's transaction is already open: this repository joins it and
    // that aggregate's save commits both. Disposing the scoped DbContext rolls back an unsaved change.
    private IDbContextTransaction? _changeTransaction;

    public ActiveProgramRepository(LifeOSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> TryAddAsync(ActiveProgram program, CancellationToken cancellationToken)
    {
        _dbContext.ActivePrograms.Add(program);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception, ActiveProgramConfiguration.ActiveIndexName))
        {
            _dbContext.Entry(program).State = EntityState.Detached;

            return false;
        }
    }

    public async Task<ActiveProgram?> GetActiveAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await Active(userId)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
    }

    // Locks the user's Active row (FOR UPDATE), then loads the tracked aggregate, so concurrent
    // changes (two workouts finishing, a stop) run one after another.
    public async Task<ActiveProgram?> GetActiveForUpdateAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_dbContext.Database.CurrentTransaction is null)
        {
            _changeTransaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        }

        await _dbContext.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM active_programs WHERE user_id = {userId} AND status = 'Active' FOR UPDATE",
            cancellationToken);

        return await Active(userId).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task SaveAsync(ActiveProgram program, CancellationToken cancellationToken)
    {
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (_changeTransaction is { } transaction)
        {
            _changeTransaction = null;
            await transaction.CommitAsync(cancellationToken);
            await transaction.DisposeAsync();
        }
    }

    private IQueryable<ActiveProgram> Active(Guid userId) =>
        _dbContext.ActivePrograms
            .Where(program => program.UserId == userId && program.Status == ActiveProgramStatus.Active)
            .Include(program => program.Completions);
}
