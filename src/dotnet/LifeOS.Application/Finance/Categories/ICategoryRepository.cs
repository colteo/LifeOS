using LifeOS.Domain.Finance.Categories;

namespace LifeOS.Application.Finance.Categories;

public interface ICategoryRepository
{
    // Returns false, persisting nothing, when the user already has a sibling with the same name
    // (same type and parent, ignoring case), e.g. created concurrently. The database enforces this.
    Task<bool> TryAddAsync(Category category, CancellationToken cancellationToken);

    // All categories in a single save. Returns false, persisting none of them, on a sibling-name
    // conflict as for TryAddAsync, or when the save lost a deadlock against a concurrent batch.
    Task<bool> TryAddRangeAsync(IReadOnlyCollection<Category> categories, CancellationToken cancellationToken);

    // Only categories owned by userId; another user's category is indistinguishable from a missing one.
    Task<Category?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Category>> GetByTypeAndParentAsync(
        Guid userId,
        CategoryType categoryType,
        Guid? parentCategoryId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Category>> GetAllAsync(Guid userId, CancellationToken cancellationToken);
}
