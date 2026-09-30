using LifeOS.Domain.Finance.Categories;

namespace LifeOS.Application.Finance.Categories.CreateCategory;

public sealed class CreateCategoryHandler
{
    private readonly ICategoryRepository _categoryRepository;
    private readonly TimeProvider _timeProvider;

    public CreateCategoryHandler(ICategoryRepository categoryRepository, TimeProvider timeProvider)
    {
        _categoryRepository = categoryRepository;
        _timeProvider = timeProvider;
    }

    public async Task<CreateCategoryResult> HandleAsync(
        Guid userId,
        CreateCategoryCommand command,
        CancellationToken cancellationToken)
    {
        Category? parent = null;

        if (command.ParentCategoryId is { } parentCategoryId)
        {
            // Scoped: another user's category is reported as a missing parent.
            parent = await _categoryRepository.GetByIdAsync(userId, parentCategoryId, cancellationToken);

            if (parent is null)
            {
                return CreateCategoryResult.ParentNotFound();
            }
        }

        var category = Category.Create(
            userId,
            command.Name,
            command.CategoryType,
            parent,
            _timeProvider.GetUtcNow());

        // Names are unique among the user's siblings (same type and parent), ignoring case.
        var siblings = await _categoryRepository.GetByTypeAndParentAsync(
            userId,
            category.CategoryType,
            category.ParentCategoryId,
            cancellationToken);

        if (siblings.Any(sibling => string.Equals(sibling.Name, category.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return CreateCategoryResult.DuplicateName();
        }

        // The database is the final backstop (concurrent creates, or case rules differing from C#).
        // Not stored: a sibling took the name, or the parent was deleted concurrently. One re-read of
        // the parent decides.
        if (!await _categoryRepository.TryAddAsync(category, cancellationToken))
        {
            return parent is not null
                && await _categoryRepository.GetByIdAsync(userId, parent.Id, cancellationToken) is null
                    ? CreateCategoryResult.ParentNotFound()
                    : CreateCategoryResult.DuplicateName();
        }

        return CreateCategoryResult.Created(new CreatedCategory(
            category.Id,
            category.Name,
            category.CategoryType,
            category.ParentCategoryId,
            category.CreatedAtUtc));
    }
}
