using LifeOS.Domain.Finance.Categories;

namespace LifeOS.Application.Finance.Categories;

public interface ICategoryRepository
{
    Task AddAsync(Category category, CancellationToken cancellationToken);

    Task<Category?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Category>> GetByTypeAndParentAsync(
        CategoryType categoryType,
        Guid? parentCategoryId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken cancellationToken);
}
