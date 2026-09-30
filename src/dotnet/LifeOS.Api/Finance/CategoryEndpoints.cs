using LifeOS.Api.Authentication;
using LifeOS.Application.Finance.Categories.CreateCategory;
using LifeOS.Application.Finance.Categories.GetCategories;
using LifeOS.Contracts.Finance.Categories;
using LifeOS.Domain.Finance.Categories;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Finance;

public static class CategoryEndpoints
{
    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Categories are owned by the authenticated user; the user id comes only from the access token.
        var categories = endpoints.MapGroup("/api/categories")
            .RequireAuthorization();

        categories.MapPost("/", CreateCategoryAsync)
            .WithName("CreateCategory");

        categories.MapGet("/", GetCategoriesAsync)
            .WithName("GetCategories");

        return endpoints;
    }

    public static async Task<Results<Created<CategoryResponse>, ValidationProblem, ProblemHttpResult>> CreateCategoryAsync(
        CreateCategoryRequest request,
        AuthenticatedUser user,
        CreateCategoryHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseCategoryType(request.Type, out var categoryType))
        {
            return ValidationError("type", "Category type is not supported.");
        }

        CreateCategoryResult result;

        try
        {
            result = await handler.HandleAsync(
                user.UserId,
                new CreateCategoryCommand(request.Name, categoryType, request.ParentCategoryId),
                cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return ValidationError(ToFieldName(exception.ParamName), exception.Message);
        }

        return result.Status switch
        {
            CreateCategoryStatus.ParentNotFound => TypedResults.Problem(
                title: "Parent category not found.",
                detail: $"Category '{request.ParentCategoryId}' does not exist.",
                statusCode: StatusCodes.Status404NotFound),

            CreateCategoryStatus.DuplicateName => TypedResults.Problem(
                title: "Category already exists.",
                detail: "A category with the same name, type and parent already exists.",
                statusCode: StatusCodes.Status409Conflict),

            _ => Created(result.Category!)
        };
    }

    public static async Task<Ok<IReadOnlyList<CategoryResponse>>> GetCategoriesAsync(
        AuthenticatedUser user,
        GetCategoriesHandler handler,
        CancellationToken cancellationToken)
    {
        var categories = await handler.HandleAsync(user.UserId, cancellationToken);

        IReadOnlyList<CategoryResponse> response = categories
            .Select(category => new CategoryResponse(
                category.Id,
                category.Name,
                category.CategoryType.ToString(),
                category.ParentCategoryId,
                category.CreatedAtUtc))
            .ToList();

        return TypedResults.Ok(response);
    }

    private static Created<CategoryResponse> Created(CreatedCategory category)
    {
        var response = new CategoryResponse(
            category.Id,
            category.Name,
            category.CategoryType.ToString(),
            category.ParentCategoryId,
            category.CreatedAtUtc);

        return TypedResults.Created($"/api/categories/{response.Id}", response);
    }

    private static bool TryParseCategoryType(string? value, out CategoryType categoryType)
    {
        // Match names only: Enum.TryParse would also accept numeric and comma-combined values.
        var name = Enum.GetNames<CategoryType>()
            .FirstOrDefault(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase));

        categoryType = name is null ? default : Enum.Parse<CategoryType>(name);

        return name is not null;
    }

    private static string ToFieldName(string? parameterName) => parameterName switch
    {
        "categoryType" => "type",
        "parent" => "parentCategoryId",
        null => "request",
        _ => parameterName
    };

    private static ValidationProblem ValidationError(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [message]
        });
}
