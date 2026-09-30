using LifeOS.Application.Finance.Categories;
using LifeOS.Domain.Finance.Categories;

namespace LifeOS.UnitTests.Fakes;

// Every read filters by userId, like the EF Core repository; ownership tests depend on it.
// Adds enforce the same rules as the ux_categories_user_sibling_name index and the parent foreign key.
internal sealed class InMemoryCategoryRepository : ICategoryRepository
{
    private readonly Lock _lock = new();

    public List<Category> Categories { get; } = [];

    // Runs just before an add checks for conflicts, to simulate a concurrent insert.
    public Action? BeforeAdd { get; set; }

    public int AddAttempts { get; private set; }

    public Task<bool> TryAddAsync(Category category, CancellationToken cancellationToken) =>
        TryAddRangeAsync([category], cancellationToken);

    public Task<bool> TryAddRangeAsync(IReadOnlyCollection<Category> categories, CancellationToken cancellationToken)
    {
        BeforeAdd?.Invoke();

        lock (_lock)
        {
            AddAttempts++;

            var all = Categories.Concat(categories).ToList();

            // Like the (parent_category_id, user_id) foreign key: a parent must be stored already or be
            // part of this batch, for the same user. Checked on the whole set, so list order is irrelevant.
            // A violation is a bug, not a conflict, so it throws like an unrecovered 23503.
            var danglingParent = categories.Any(category =>
                category.ParentCategoryId is { } parentId
                && !all.Any(candidate => candidate.Id == parentId && candidate.UserId == category.UserId));

            if (danglingParent)
            {
                throw new InvalidOperationException("A category references a parent that does not exist for its user.");
            }

            // Like the index: (user, type, parent, lower(name)) with top-level names as siblings.
            var hasDuplicate = all
                .GroupBy(category => (category.UserId, category.CategoryType, category.ParentCategoryId, Name: category.Name.ToLowerInvariant()))
                .Any(group => group.Count() > 1);

            if (hasDuplicate)
            {
                return Task.FromResult(false);
            }

            Categories.AddRange(categories);

            return Task.FromResult(true);
        }
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
