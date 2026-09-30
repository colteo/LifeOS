using LifeOS.Application.Finance.Categories;
using LifeOS.Domain.Finance.Categories;

namespace LifeOS.UnitTests.Fakes;

// Every read filters by userId, like the EF Core repository; ownership tests depend on it.
internal sealed class InMemoryCategoryRepository : ICategoryRepository
{
    private readonly Lock _lock = new();

    public List<Category> Categories { get; } = [];

    public Task AddAsync(Category category, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Categories.Add(category);
        }

        return Task.CompletedTask;
    }

    public Task<Category?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Categories.SingleOrDefault(category => category.UserId == userId && category.Id == id));
        }
    }

    public Task<IReadOnlyList<Category>> GetByTypeAndParentAsync(
        Guid userId,
        CategoryType categoryType,
        Guid? parentCategoryId,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Category>>(Categories
                .Where(category => category.UserId == userId
                    && category.CategoryType == categoryType
                    && category.ParentCategoryId == parentCategoryId)
                .ToList());
        }
    }

    public Task<IReadOnlyList<Category>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Category>>(Categories.Where(category => category.UserId == userId).ToList());
        }
    }
}
