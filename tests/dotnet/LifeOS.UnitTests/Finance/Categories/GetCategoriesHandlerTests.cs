using LifeOS.Application.Finance.Categories.GetCategories;
using LifeOS.Domain.Finance.Categories;
using LifeOS.UnitTests.Fakes;

namespace LifeOS.UnitTests.Finance.Categories;

public class GetCategoriesHandlerTests
{
    private static readonly DateTimeOffset CreatedAtUtc = new(2026, 9, 29, 10, 30, 0, TimeSpan.Zero);

    private readonly InMemoryCategoryRepository _repository = new();
    private readonly GetCategoriesHandler _handler;

    public GetCategoriesHandlerTests()
    {
        _handler = new GetCategoriesHandler(_repository);
    }

    [Fact]
    public async Task HandleAsync_ReturnsAllCategoriesWithParentIds()
    {
        var auto = Add("Auto", CategoryType.Expense);
        var benzina = Add("Benzina", CategoryType.Expense, auto);

        var result = await _handler.HandleAsync(TestUsers.A, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(
            new CategorySummary(benzina.Id, "Benzina", CategoryType.Expense, auto.Id, CreatedAtUtc),
            result);
    }

    [Fact]
    public async Task HandleAsync_OrdersByTypeThenTopLevelFirstThenName()
    {
        var mangiareFuori = Add("Mangiare fuori", CategoryType.Expense);
        var auto = Add("Auto", CategoryType.Expense);
        Add("Bar", CategoryType.Expense, mangiareFuori);
        Add("Benzina", CategoryType.Expense, auto);
        Add("Stipendio", CategoryType.Income);
        Add("bonus", CategoryType.Income);
        Add("Altro", CategoryType.Expense);

        var result = await _handler.HandleAsync(TestUsers.A, CancellationToken.None);

        Assert.Equal(
            ["bonus", "Stipendio", "Altro", "Auto", "Mangiare fuori", "Bar", "Benzina"],
            result.Select(category => category.Name));
    }

    [Fact]
    public async Task HandleAsync_WithNoCategories_ReturnsEmptyCollection()
    {
        var result = await _handler.HandleAsync(TestUsers.A, CancellationToken.None);

        Assert.Empty(result);
    }

    private Category Add(string name, CategoryType categoryType, Category? parent = null)
    {
        var category = Category.Create(TestUsers.A, name, categoryType, parent, CreatedAtUtc);
        _repository.Categories.Add(category);

        return category;
    }
}
