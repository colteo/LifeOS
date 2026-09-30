using LifeOS.Application.Finance.Categories;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Finance.Categories;

internal sealed class CategoryRepository : ICategoryRepository
{
    private readonly LifeOSDbContext _dbContext;

    public CategoryRepository(LifeOSDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(Category category, CancellationToken cancellationToken)
    {
        _dbContext.Categories.Add(category);

        await _dbContext.SaveChangesAsync(cancellationToken);
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
}
