using LifeOS.Domain.Finance.Categories;

namespace LifeOS.Application.Finance.Categories;

public interface ICategoryRepository
{
    Task AddAsync(Category category, CancellationToken cancellationToken);

    // Only categories owned by userId; another user's category is indistinguishable from a missing one.
    Task<Category?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Category>> GetByTypeAndParentAsync(
        Guid userId,
        CategoryType categoryType,
        Guid? parentCategoryId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Category>> GetAllAsync(Guid userId, CancellationToken cancellationToken);
}
