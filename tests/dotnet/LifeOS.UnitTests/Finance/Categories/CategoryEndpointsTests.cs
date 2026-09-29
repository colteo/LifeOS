using LifeOS.Api.Finance;
using LifeOS.Application.Finance.Categories.CreateCategory;
using LifeOS.Application.Finance.Categories.GetCategories;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Domain.Finance.Categories;
using LifeOS.UnitTests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.UnitTests.Finance.Categories;

public class CategoryEndpointsTests
{
    private static readonly DateTimeOffset UtcNow = new(2026, 9, 29, 10, 30, 0, TimeSpan.Zero);

    private readonly InMemoryCategoryRepository _repository = new();
    private readonly CreateCategoryHandler _createHandler;
    private readonly GetCategoriesHandler _getHandler;

    public CategoryEndpointsTests()
    {
        _createHandler = new CreateCategoryHandler(_repository, new FixedTimeProvider(UtcNow));
        _getHandler = new GetCategoriesHandler(_repository);
    }

    [Fact]
    public async Task CreateCategory_TopLevel_ReturnsCreated()
    {
        var request = new CreateCategoryRequest("Auto", "expense", null);

        var result = await CategoryEndpoints.CreateCategoryAsync(request, _createHandler, CancellationToken.None);

        var created = Assert.IsType<Created<CategoryResponse>>(result.Result);
        var response = Assert.IsType<CategoryResponse>(created.Value);
        Assert.Equal($"/api/categories/{response.Id}", created.Location);
        Assert.Equal("Auto", response.Name);
        Assert.Equal("Expense", response.Type);
        Assert.Null(response.ParentCategoryId);
        Assert.Equal(UtcNow, response.CreatedAtUtc);
    }

    [Fact]
    public async Task CreateCategory_Subcategory_ReturnsCreatedWithParentId()
    {
        var parent = AddExisting("Auto", CategoryType.Expense);
        var request = new CreateCategoryRequest("Benzina", "Expense", parent.Id);

        var result = await CategoryEndpoints.CreateCategoryAsync(request, _createHandler, CancellationToken.None);

        var created = Assert.IsType<Created<CategoryResponse>>(result.Result);
        Assert.Equal(parent.Id, created.Value!.ParentCategoryId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Transfer")]
    [InlineData("1")]
    [InlineData("Income,Expense")]
    public async Task CreateCategory_WithInvalidType_ReturnsValidationProblem(string? type)
    {
        var request = new CreateCategoryRequest("Auto", type!, null);

        var result = await CategoryEndpoints.CreateCategoryAsync(request, _createHandler, CancellationToken.None);

        AssertValidationProblem(result.Result, "type");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateCategory_WithBlankName_ReturnsValidationProblem(string name)
    {
        var request = new CreateCategoryRequest(name, "Expense", null);

        var result = await CategoryEndpoints.CreateCategoryAsync(request, _createHandler, CancellationToken.None);

        AssertValidationProblem(result.Result, "name");
    }

    [Fact]
    public async Task CreateCategory_WithParentOfDifferentType_ReturnsValidationProblem()
    {
        var incomeParent = AddExisting("Stipendio", CategoryType.Income);
        var request = new CreateCategoryRequest("Benzina", "Expense", incomeParent.Id);

        var result = await CategoryEndpoints.CreateCategoryAsync(request, _createHandler, CancellationToken.None);

        AssertValidationProblem(result.Result, "parentCategoryId");
    }

    [Fact]
    public async Task CreateCategory_WithSubcategoryAsParent_ReturnsValidationProblem()
    {
        var parent = AddExisting("Auto", CategoryType.Expense);
        var child = AddExisting("Benzina", CategoryType.Expense, parent);
        var request = new CreateCategoryRequest("Diesel", "Expense", child.Id);

        var result = await CategoryEndpoints.CreateCategoryAsync(request, _createHandler, CancellationToken.None);

        AssertValidationProblem(result.Result, "parentCategoryId");
    }

    [Fact]
    public async Task CreateCategory_WithMissingParent_ReturnsNotFound()
    {
        var request = new CreateCategoryRequest("Benzina", "Expense", Guid.CreateVersion7());

        var result = await CategoryEndpoints.CreateCategoryAsync(request, _createHandler, CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Empty(_repository.Categories);
    }

    [Fact]
    public async Task CreateCategory_WithDuplicateSiblingName_ReturnsConflict()
    {
        AddExisting("Auto", CategoryType.Expense);
        var request = new CreateCategoryRequest("AUTO", "Expense", null);

        var result = await CategoryEndpoints.CreateCategoryAsync(request, _createHandler, CancellationToken.None);

        var problem = Assert.IsType<ProblemHttpResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.Single(_repository.Categories);
    }

    [Fact]
    public async Task GetCategories_ReturnsOkWithFlatOrderedList()
    {
        var auto = AddExisting("Auto", CategoryType.Expense);
        var benzina = AddExisting("Benzina", CategoryType.Expense, auto);
        AddExisting("Stipendio", CategoryType.Income);

        var result = await CategoryEndpoints.GetCategoriesAsync(_getHandler, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal(["Stipendio", "Auto", "Benzina"], result.Value!.Select(category => category.Name));

        var child = result.Value!.Single(category => category.Id == benzina.Id);
        Assert.Equal("Expense", child.Type);
        Assert.Equal(auto.Id, child.ParentCategoryId);
    }

    [Fact]
    public async Task GetCategories_WithNoCategories_ReturnsOkWithEmptyList()
    {
        var result = await CategoryEndpoints.GetCategoriesAsync(_getHandler, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.NotNull(result.Value);
        Assert.Empty(result.Value);
    }

    private Category AddExisting(string name, CategoryType categoryType, Category? parent = null)
    {
        var category = Category.Create(name, categoryType, parent, UtcNow);
        _repository.Categories.Add(category);

        return category;
    }

    private void AssertValidationProblem(IResult result, string expectedField)
    {
        var problem = Assert.IsType<ValidationProblem>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Contains(expectedField, problem.ProblemDetails.Errors.Keys);
    }
}
