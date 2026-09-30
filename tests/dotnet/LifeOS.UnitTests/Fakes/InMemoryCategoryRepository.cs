using System.Reflection;
using LifeOS.Application.Finance.Categories;
using LifeOS.Domain.Finance.Categories;

namespace LifeOS.UnitTests.Fakes;

// Every read filters by userId, like the EF Core repository; ownership tests depend on it. Reads
// return detached copies, like AsNoTracking, so a handler's in-memory changes are only "stored"
// when it persists them. Writes enforce the same rules as the ux_categories_user_sibling_name index
// and the parent foreign key.
internal sealed class InMemoryCategoryRepository : ICategoryRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Lock _lock = new();

    public List<Category> Categories { get; } = [];

    // Runs just before an add checks for conflicts, to simulate a concurrent insert or delete.
    public Action? BeforeAdd { get; set; }

    // Runs just before TryRenameAsync / DeleteAsync touch the stored rows, to simulate a concurrent
    // request.
    public Action? BeforeWrite { get; set; }

    // When set, DeleteAsync reports this outcome without deleting, as if a restricting foreign key
    // (e.g. a concurrent transaction) had rejected the delete.
    public CategoryDeleteOutcome? RejectDeleteWith { get; set; }

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
            // part of this batch, for the same user. Checked on the whole set, so list order is
            // irrelevant. Like the recovered 23503, nothing is stored and the result is false.
            var danglingParent = categories.Any(category =>
                category.ParentCategoryId is { } parentId
                && !all.Any(candidate => candidate.Id == parentId && candidate.UserId == category.UserId));

            if (danglingParent || HasDuplicateSibling(all))
            {
                return Task.FromResult(false);
            }

            Categories.AddRange(categories.Select(Clone));

            return Task.FromResult(true);
        }
    }

    public Task<Category?> GetByIdAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var category = Categories.SingleOrDefault(category => category.UserId == userId && category.Id == id);

            return Task.FromResult(category is null ? null : Clone(category));
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
                .Select(Clone)
                .ToList());
        }
    }

    public Task<IReadOnlyList<Category>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Category>>(
                Categories.Where(category => category.UserId == userId).Select(Clone).ToList());
        }
    }

    // Replaces the stored row with the same id and owner, like the conditional UPDATE, unless the new
    // name clashes with a sibling (like the index).
    public Task<CategoryRenameOutcome> TryRenameAsync(Category category, CancellationToken cancellationToken)
    {
        BeforeWrite?.Invoke();

        lock (_lock)
        {
            var index = Categories.FindIndex(stored => stored.Id == category.Id && stored.UserId == category.UserId);

            if (index < 0)
            {
                return Task.FromResult(CategoryRenameOutcome.NotFound);
            }

            var renamed = Categories.Select((stored, position) => position == index ? category : stored).ToList();

            if (HasDuplicateSibling(renamed))
            {
                return Task.FromResult(CategoryRenameOutcome.DuplicateName);
            }

            Categories[index] = Clone(category);

            return Task.FromResult(CategoryRenameOutcome.Renamed);
        }
    }

    // Like ON DELETE RESTRICT on the parent reference: a category with children is kept.
    public Task<CategoryDeleteOutcome> DeleteAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken)
    {
        BeforeWrite?.Invoke();

        lock (_lock)
        {
            if (!Categories.Any(category => category.UserId == userId && category.Id == categoryId))
            {
                return Task.FromResult(CategoryDeleteOutcome.NotFound);
            }

            if (Categories.Any(category => category.UserId == userId && category.ParentCategoryId == categoryId))
            {
                return Task.FromResult(CategoryDeleteOutcome.HasSubcategories);
            }

            if (RejectDeleteWith is { } outcome)
            {
                return Task.FromResult(outcome);
            }

            Categories.RemoveAll(category => category.UserId == userId && category.Id == categoryId);

            return Task.FromResult(CategoryDeleteOutcome.Deleted);
        }
    }

    // The stored row, for assertions.
    public Category Stored(Guid categoryId)
    {
        lock (_lock)
        {
            return Categories.Single(category => category.Id == categoryId);
        }
    }

    // Like the index: (user, type, parent, lower(name)) with top-level names as siblings.
    private static bool HasDuplicateSibling(IEnumerable<Category> categories) =>
        categories
            .GroupBy(category => (category.UserId, category.CategoryType, category.ParentCategoryId, Name: category.Name.ToLowerInvariant()))
            .Any(group => group.Count() > 1);

    private static Category Clone(Category category) => (Category)CloneMethod.Invoke(category, null)!;
}
