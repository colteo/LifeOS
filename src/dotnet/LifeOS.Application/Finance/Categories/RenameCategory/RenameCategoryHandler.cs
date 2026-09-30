using LifeOS.Application.Finance.Categories.GetCategories;

namespace LifeOS.Application.Finance.Categories.RenameCategory;

// Renames a top-level category or a subcategory. Names are current metadata: existing transactions
// show the new name. Names stay unique among siblings (same type and parent), ignoring case; a
// case-only rename of the category itself is allowed.
public sealed class RenameCategoryHandler
{
    private readonly ICategoryRepository _categoryRepository;

    public RenameCategoryHandler(ICategoryRepository categoryRepository)
    {
        _categoryRepository = categoryRepository;
    }

    // Throws ArgumentException for a blank name.
    public async Task<RenameCategoryResult> HandleAsync(
        Guid userId,
        RenameCategoryCommand command,
        CancellationToken cancellationToken)
    {
        // Scoped: another user's category is reported exactly like a missing one.
        var category = await _categoryRepository.GetByIdAsync(userId, command.CategoryId, cancellationToken);

        if (category is null)
        {
            return RenameCategoryResult.NotFound();
        }

        category.Rename(command.Name);

        var siblings = await _categoryRepository.GetByTypeAndParentAsync(
            userId,
            category.CategoryType,
            category.ParentCategoryId,
            cancellationToken);

        if (siblings.Any(sibling =>
                sibling.Id != category.Id
                && string.Equals(sibling.Name, category.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return RenameCategoryResult.DuplicateName();
        }

        // The database is the final backstop: a concurrent rename or create to the same name, or a
        // concurrent delete.
        return await _categoryRepository.TryRenameAsync(category, cancellationToken) switch
        {
            CategoryRenameOutcome.Renamed => RenameCategoryResult.Renamed(new CategorySummary(
                category.Id,
                category.Name,
                category.CategoryType,
                category.ParentCategoryId,
                category.CreatedAtUtc)),
            CategoryRenameOutcome.DuplicateName => RenameCategoryResult.DuplicateName(),
            _ => RenameCategoryResult.NotFound()
        };
    }
}
