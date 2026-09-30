using LifeOS.Application.Finance.Categories;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LifeOS.Infrastructure.Finance.Categories;

internal sealed class CategoryRepository : ICategoryRepository
{
    private readonly LifeOSDbContext _dbContext;

    public CategoryRepository(LifeOSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<bool> TryAddAsync(Category category, CancellationToken cancellationToken) =>
        AddAsync([category], deadlockIsRecoverable: false, cancellationToken);

    // Besides a sibling-name conflict, a batch can lose a deadlock (40P01) against a concurrent batch
    // inserting the same names in a different row order. PostgreSQL rolls the losing transaction back,
    // so, as for a conflict, nothing of this batch was committed and the caller may reconcile.
    public Task<bool> TryAddRangeAsync(IReadOnlyCollection<Category> categories, CancellationToken cancellationToken) =>
        AddAsync(categories, deadlockIsRecoverable: true, cancellationToken);

    private async Task<bool> AddAsync(
        IReadOnlyCollection<Category> categories,
        bool deadlockIsRecoverable,
        CancellationToken cancellationToken)
    {
        _dbContext.Categories.AddRange(categories);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException exception) when (IsDuplicateSiblingName(exception))
        {
            DetachUnsaved(categories);

            return false;
        }
        // Npgsql marks a deadlock as transient, so EF Core wraps it in an InvalidOperationException.
        // Only this exact SQLSTATE is recovered; every other error still propagates.
        catch (Exception exception) when (deadlockIsRecoverable && IsDeadlock(exception))
        {
            DetachUnsaved(categories);

            return false;
        }
    }

    // Nothing of the failed save was committed. Detach its categories, so a re-read/reconcile later in
    // this request does not insert them again with its own SaveChanges.
    private void DetachUnsaved(IReadOnlyCollection<Category> categories)
    {
        foreach (var category in categories)
        {
            var entry = _dbContext.Entry(category);

            if (entry.State == EntityState.Added)
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    public async Task<Category?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        return await _dbContext.Categories
            .AsNoTracking()
            .SingleOrDefaultAsync(category => category.UserId == userId && category.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<Category>> GetByTypeAndParentAsync(
        Guid userId,
        CategoryType categoryType,
        Guid? parentCategoryId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.Categories
            .AsNoTracking()
            .Where(category => category.UserId == userId
                && category.CategoryType == categoryType
                && category.ParentCategoryId == parentCategoryId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Category>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await _dbContext.Categories
            .AsNoTracking()
            .Where(category => category.UserId == userId)
            .ToListAsync(cancellationToken);
    }

    // Only the sibling-name index; any other unique violation still propagates.
    private static bool IsDuplicateSiblingName(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: CategoryConfiguration.SiblingNameIndexName
        };

    private static bool IsDeadlock(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected })
            {
                return true;
            }
        }

        return false;
    }
}
