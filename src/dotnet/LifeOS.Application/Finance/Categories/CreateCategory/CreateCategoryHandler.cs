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
        CreateCategoryCommand command,
        CancellationToken cancellationToken)
    {
        Category? parent = null;

        if (command.ParentCategoryId is { } parentCategoryId)
        {
            parent = await _categoryRepository.GetByIdAsync(parentCategoryId, cancellationToken);

            if (parent is null)
            {
                return CreateCategoryResult.ParentNotFound();
            }
        }

        var category = Category.Create(
            command.Name,
            command.CategoryType,
            parent,
            _timeProvider.GetUtcNow());

        // Names are unique among siblings (same type and parent), ignoring case.
        var siblings = await _categoryRepository.GetByTypeAndParentAsync(
            category.CategoryType,
            category.ParentCategoryId,
            cancellationToken);

        if (siblings.Any(sibling => string.Equals(sibling.Name, category.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return CreateCategoryResult.DuplicateName();
        }

        await _categoryRepository.AddAsync(category, cancellationToken);

        return CreateCategoryResult.Created(new CreatedCategory(
            category.Id,
            category.Name,
            category.CategoryType,
            category.ParentCategoryId,
            category.CreatedAtUtc));
    }
}
