namespace LifeOS.Application.Finance.Categories.GetCategories;

public sealed class GetCategoriesHandler
{
    private readonly ICategoryRepository _categoryRepository;

    public GetCategoriesHandler(ICategoryRepository categoryRepository)
    {
        _categoryRepository = categoryRepository;
    }

    public async Task<IReadOnlyList<CategorySummary>> HandleAsync(CancellationToken cancellationToken)
    {
        var categories = await _categoryRepository.GetAllAsync(cancellationToken);

        // Deterministic order: type, then top-level before subcategories, then name.
        // Id is the final tie-breaker for equal names under different parents.
        return categories
            .OrderBy(category => category.CategoryType)
            .ThenBy(category => category.ParentCategoryId is null ? 0 : 1)
            .ThenBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(category => category.Id)
            .Select(category => new CategorySummary(
                category.Id,
                category.Name,
                category.CategoryType,
                category.ParentCategoryId,
                category.CreatedAtUtc))
            .ToList();
    }
}
