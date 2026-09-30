using LifeOS.Application.Finance.Categories;
using LifeOS.Domain.Finance.Categories;

namespace LifeOS.UnitTests.Fakes;

// Every read filters by userId, like the EF Core repository; ownership tests depend on it.
// Adds enforce the same rule as the ux_categories_user_sibling_name index.
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
