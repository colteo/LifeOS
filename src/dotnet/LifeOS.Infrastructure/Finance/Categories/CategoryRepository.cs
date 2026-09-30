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
        TryAddRangeAsync([category], cancellationToken);

    public async Task<bool> TryAddRangeAsync(IReadOnlyCollection<Category> categories, CancellationToken cancellationToken)
    {
        _dbContext.Categories.AddRange(categories);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (DbUpdateException exception) when (IsDuplicateSiblingName(exception))
        {
            // Nothing was saved. Detach every category of the failed save, so a re-read/reconcile
            // later in this request does not insert them again with its own SaveChanges.
            foreach (var entry in _dbContext.ChangeTracker.Entries<Category>()
                         .Where(entry => entry.State == EntityState.Added)
                         .ToList())
            {
                entry.State = EntityState.Detached;
            }

            return false;
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
}
