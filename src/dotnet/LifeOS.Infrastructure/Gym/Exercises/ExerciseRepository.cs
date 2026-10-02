using LifeOS.Application.Gym.Exercises;
using LifeOS.Domain.Gym.Exercises;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Gym.Exercises;

internal sealed class ExerciseRepository : IExerciseRepository
{
    private readonly LifeOSDbContext _dbContext;

    public ExerciseRepository(LifeOSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> TryAddAsync(Exercise exercise, CancellationToken cancellationToken)
    {
        _dbContext.Exercises.Add(exercise);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception, ExerciseConfiguration.NameIndexName))
        {
            // Nothing was committed; detach it so a later save in this request does not retry it.
            _dbContext.Entry(exercise).State = EntityState.Detached;

            return false;
        }
    }

    public async Task<IReadOnlyList<Exercise>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Exercises
            .AsNoTracking()
            .Where(exercise => exercise.UserId == userId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Exercise>> GetByIdsAsync(
        Guid userId,
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        var requested = ids.ToList();

        return await _dbContext.Exercises
            .AsNoTracking()
            .Where(exercise => exercise.UserId == userId && requested.Contains(exercise.Id))
            .ToListAsync(cancellationToken);
    }
}
