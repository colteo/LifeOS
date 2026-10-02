using LifeOS.Application.Finance.Transactions;

namespace LifeOS.Application.Finance.Categories.DeleteCategory;

// Deletes an unused category: one without subcategories that no transaction references. Nothing is
// cascaded and history is never modified.
public sealed class DeleteCategoryHandler
{
    private readonly ICategoryRepository _categoryRepository;
    private readonly ITransactionRepository _transactionRepository;

    public DeleteCategoryHandler(ICategoryRepository categoryRepository, ITransactionRepository transactionRepository)
    {
        _categoryRepository = categoryRepository;
        _transactionRepository = transactionRepository;
    }

    public async Task<DeleteCategoryResult> HandleAsync(Guid userId, Guid categoryId, CancellationToken cancellationToken)
    {
        // Scoped: another user's category is reported exactly like a missing one.
        var category = await _categoryRepository.GetByIdAsync(userId, categoryId, cancellationToken);

        if (category is null)
        {
            return DeleteCategoryResult.NotFound;
        }

        var children = await _categoryRepository.GetByTypeAndParentAsync(
            userId,
            category.CategoryType,
            category.Id,
            cancellationToken);

        if (children.Count > 0)
        {
            return DeleteCategoryResult.HasSubcategories;
        }

        if (await _transactionRepository.AnyReferencingCategoryAsync(userId, categoryId, cancellationToken))
        {
            return DeleteCategoryResult.InUse;
        }

        // The database is the final backstop: a subcategory or transaction created after the checks
        // above makes the delete fail, and the outcome says why.
        return await _categoryRepository.DeleteAsync(userId, categoryId, cancellationToken) switch
        {
            CategoryDeleteOutcome.Deleted => DeleteCategoryResult.Deleted,
            CategoryDeleteOutcome.HasSubcategories => DeleteCategoryResult.HasSubcategories,
            CategoryDeleteOutcome.InUse => DeleteCategoryResult.InUse,
            CategoryDeleteOutcome.HasRecurringRules => DeleteCategoryResult.HasRecurringRules,
            _ => DeleteCategoryResult.NotFound
        };
    }
}
