using LifeOS.Application.Finance.Categories;
using LifeOS.Domain.Finance.Categories;

namespace LifeOS.UnitTests.Fakes;

internal sealed class InMemoryCategoryRepository : ICategoryRepository
{
    public List<Category> Categories { get; } = [];

    public Task AddAsync(Category category, CancellationToken cancellationToken)
    {
        Categories.Add(category);

        return Task.CompletedTask;
    }

    public Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        return Task.FromResult(Categories.SingleOrDefault(category => category.Id == id));
    }

    public Task<IReadOnlyList<Category>> GetByTypeAndParentAsync(
        CategoryType categoryType,
        Guid? parentCategoryId,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<Category>>(Categories
            .Where(category => category.CategoryType == categoryType
                && category.ParentCategoryId == parentCategoryId)
            .ToList());
    }

    public Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<Category>>(Categories.ToList());
    }
}
