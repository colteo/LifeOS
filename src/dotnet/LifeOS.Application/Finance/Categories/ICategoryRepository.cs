using LifeOS.Domain.Finance.Categories;

namespace LifeOS.Application.Finance.Categories;

public interface ICategoryRepository
{
    // Returns false, persisting nothing, when the user already has a sibling with the same name
    // (same type and parent, ignoring case), e.g. created concurrently, or when the parent no longer
    // exists (deleted concurrently). The database enforces both.
    Task<bool> TryAddAsync(Category category, CancellationToken cancellationToken);

    // All categories in a single save. Returns false, persisting none of them, for the same reasons
    // as TryAddAsync, or when the save lost a deadlock against a concurrent batch.
    Task<bool> TryAddRangeAsync(IReadOnlyCollection<Category> categories, CancellationToken cancellationToken);

    // Only categories owned by userId; another user's category is indistinguishable from a missing one.
    Task<Category?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Category>> GetByTypeAndParentAsync(
        Guid userId,
        CategoryType categoryType,
        Guid? parentCategoryId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<Category>> GetAllAsync(Guid userId, CancellationToken cancellationToken);

    // Stores the category's name on the row with the same id and owner. The database rejects a name
    // already used by a sibling (ignoring case).
    Task<CategoryRenameOutcome> TryRenameAsync(Category category, CancellationToken cancellationToken);

    // Deletes one category, never its children. The database restricts deleting a category that has
    // subcategories or that transactions reference.
    Task<CategoryDeleteOutcome> DeleteAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken);
}

public enum CategoryRenameOutcome
{
    Renamed,

    // No category with this id for this user (e.g. deleted concurrently).
    NotFound,

    // A sibling already has this name; nothing was written.
    DuplicateName
}

public enum CategoryDeleteOutcome
{
    Deleted,

    // No category with this id for this user.
    NotFound,

    // Subcategories reference it; nothing was deleted.
    HasSubcategories,

    // Transactions reference it; nothing was deleted.
    InUse,
    HasRecurringRules
}
