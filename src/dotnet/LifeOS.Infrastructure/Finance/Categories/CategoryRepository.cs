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
        // The parent was deleted after the caller read it (23503 on the parent foreign key only).
        // Nothing was committed; the caller re-reads, as for a conflict.
        catch (DbUpdateException exception) when (PostgresErrors.IsForeignKeyViolation(exception, CategoryConfiguration.ParentForeignKeyName))
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

    // Conditional on id and owner; only the name is written (never the type or the parent).
    public async Task<CategoryRenameOutcome> TryRenameAsync(Category category, CancellationToken cancellationToken)
    {
        try
        {
            var updated = await _dbContext.Categories
                .Where(stored => stored.Id == category.Id && stored.UserId == category.UserId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(stored => stored.Name, category.Name), cancellationToken);

            return updated == 1 ? CategoryRenameOutcome.Renamed : CategoryRenameOutcome.NotFound;
        }
        catch (Exception exception) when (PostgresErrors.IsUniqueViolation(exception, CategoryConfiguration.SiblingNameIndexName))
        {
            return CategoryRenameOutcome.DuplicateName;
        }
    }

    // One statement, one category: children are never deleted with it. The restricting foreign keys
    // decide the expected failures (23001, or 23503 before PostgreSQL 18), recognized by name; any
    // other error still propagates.
    public async Task<CategoryDeleteOutcome> DeleteAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await _dbContext.Categories
                .Where(category => category.Id == categoryId && category.UserId == userId)
                .ExecuteDeleteAsync(cancellationToken);

            return deleted == 1 ? CategoryDeleteOutcome.Deleted : CategoryDeleteOutcome.NotFound;
        }
        catch (Exception exception) when (PostgresErrors.IsDeleteBlockedByReference(exception, CategoryConfiguration.ParentForeignKeyName))
        {
            return CategoryDeleteOutcome.HasSubcategories;
        }
        catch (Exception exception) when (PostgresErrors.IsDeleteBlockedByReference(exception, TransactionConfiguration.CategoryForeignKeyName))
        {
            return CategoryDeleteOutcome.InUse;
        }
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
