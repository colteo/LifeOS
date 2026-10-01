using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Analytics;

// Expenses and Income are positive quantities; NetFlow may be negative.
public sealed record CurrencyAnalytics(
    string Currency,
    decimal Expenses,
    decimal Income,
    IReadOnlyList<ExpenseCategoryAnalytics> ExpenseCategories)
{
    public decimal NetFlow => Income - Expenses;
}

// CategoryId is null only for the defensive "Unknown category" group (see the calculator).
public sealed record ExpenseCategoryAnalytics(
    Guid? CategoryId,
    string Name,
    decimal Amount,
    decimal DirectAmount,
    IReadOnlyList<ExpenseSubcategoryAnalytics> Subcategories);

public sealed record ExpenseSubcategoryAnalytics(Guid CategoryId, string Name, decimal Amount);

// Monthly external cash flow from one user's transactions and categories. Pure and in memory.
//
// Rules:
//   - Income adds to Income and Expense to Expenses; transfers (money moving between the user's own
//     accounts) are not cash flow and are ignored. Opening balances are not transactions at all.
//   - One independent result per currency; currencies are never added together.
//   - Expenses are grouped by the current category tree, by id, with current names: an expense on a
//     top-level category counts as that category's direct amount; an expense on a subcategory rolls
//     up into its parent and appears in the parent's subcategory breakdown.
//   - Nothing is ever dropped, so the groups always add up to Expenses. A category that is not part
//     of a valid two-level tree (missing parent, parent that is itself a subcategory, or a parent of
//     another type) forms its own group. An expense whose category cannot be found at all (impossible
//     while referenced categories cannot be deleted) goes to one "Unknown category" group without an id.
//   - Groups and subcategories are sorted by amount (largest first), then name, then id.
public static class MonthlyAnalyticsCalculator
{
    public const string UnknownCategoryName = "Unknown category";

    public static IReadOnlyList<CurrencyAnalytics> Build(
        IEnumerable<Transaction> transactions,
        IEnumerable<Category> categories)
    {
        var categoriesById = categories.ToDictionary(category => category.Id);

        return transactions
            .Where(transaction => transaction.TransactionType is TransactionType.Income or TransactionType.Expense)
            .GroupBy(transaction => transaction.Currency, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => BuildCurrency(group.Key, group.ToList(), categoriesById))
            .ToList();
    }

    private static CurrencyAnalytics BuildCurrency(
        string currency,
        IReadOnlyList<Transaction> transactions,
        IReadOnlyDictionary<Guid, Category> categoriesById)
    {
        var expenses = transactions.Where(transaction => transaction.TransactionType == TransactionType.Expense).ToList();
        var income = transactions
            .Where(transaction => transaction.TransactionType == TransactionType.Income)
            .Sum(transaction => transaction.Amount);

        var groups = new Dictionary<Guid, GroupBuilder>();
        GroupBuilder? unknown = null;

        foreach (var expense in expenses)
        {
            if (expense.CategoryId is not { } categoryId || !categoriesById.TryGetValue(categoryId, out var category))
            {
                unknown ??= new GroupBuilder(null, UnknownCategoryName);
                unknown.AddDirect(expense.Amount);
                continue;
            }

            if (ValidParent(category, categoriesById) is { } parent)
            {
                GroupFor(groups, parent).AddChild(category, expense.Amount);
            }
            else
            {
                // A top-level category, or one outside a valid tree: its own group.
                GroupFor(groups, category).AddDirect(expense.Amount);
            }
        }

        var expenseCategories = groups.Values
            .Concat(unknown is null ? [] : [unknown])
            .Select(group => group.Build())
            .OrderByDescending(group => group.Amount)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.CategoryId)
            .ToList();

        return new CurrencyAnalytics(currency, expenses.Sum(expense => expense.Amount), income, expenseCategories);
    }

    // The parent a subcategory rolls up into: an existing top-level category of the same type.
    private static Category? ValidParent(Category category, IReadOnlyDictionary<Guid, Category> categoriesById) =>
        category.ParentCategoryId is { } parentId
        && categoriesById.TryGetValue(parentId, out var parent)
        && parent.ParentCategoryId is null
        && parent.CategoryType == category.CategoryType
            ? parent
            : null;

    private static GroupBuilder GroupFor(Dictionary<Guid, GroupBuilder> groups, Category category)
    {
        if (!groups.TryGetValue(category.Id, out var group))
        {
            group = new GroupBuilder(category.Id, category.Name);
            groups.Add(category.Id, group);
        }

        return group;
    }

    private sealed class GroupBuilder(Guid? categoryId, string name)
    {
        private readonly Dictionary<Guid, (string Name, decimal Amount)> _subcategories = [];
        private decimal _direct;

        public void AddDirect(decimal amount) => _direct += amount;

        public void AddChild(Category child, decimal amount)
        {
            var current = _subcategories.GetValueOrDefault(child.Id, (Name: child.Name, Amount: 0m));
            _subcategories[child.Id] = (current.Name, current.Amount + amount);
        }

        public ExpenseCategoryAnalytics Build()
        {
            var subcategories = _subcategories
                .Select(entry => new ExpenseSubcategoryAnalytics(entry.Key, entry.Value.Name, entry.Value.Amount))
                .OrderByDescending(subcategory => subcategory.Amount)
                .ThenBy(subcategory => subcategory.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(subcategory => subcategory.CategoryId)
                .ToList();

            return new ExpenseCategoryAnalytics(
                categoryId,
                name,
                _direct + subcategories.Sum(subcategory => subcategory.Amount),
                _direct,
                subcategories);
        }
    }
}
