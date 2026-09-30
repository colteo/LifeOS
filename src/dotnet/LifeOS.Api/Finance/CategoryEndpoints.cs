using LifeOS.Api.Authentication;
using LifeOS.Application.Finance.Categories.CreateCategory;
using LifeOS.Application.Finance.Categories.DeleteCategory;
using LifeOS.Application.Finance.Categories.GetCategories;
using LifeOS.Application.Finance.Categories.RenameCategory;
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

        // Category management: only the name is editable; unused categories can be deleted.
        categories.MapPut("/{categoryId:guid}", UpdateCategoryAsync)
            .WithName("UpdateCategory");

        categories.MapDelete("/{categoryId:guid}", DeleteCategoryAsync)
            .WithName("DeleteCategory");

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

    // 200 with the renamed category, 404 for a missing (or another user's) category, 409 when a
    // sibling already has the name.
    public static async Task<Results<Ok<CategoryResponse>, ValidationProblem, ProblemHttpResult>> UpdateCategoryAsync(
        Guid categoryId,
        UpdateCategoryRequest request,
        AuthenticatedUser user,
        RenameCategoryHandler handler,
        CancellationToken cancellationToken)
    {
        RenameCategoryResult result;

        try
        {
            result = await handler.HandleAsync(user.UserId, new RenameCategoryCommand(categoryId, request.Name), cancellationToken);
        }
        catch (ArgumentException exception)
        {
            return ValidationError(ToFieldName(exception.ParamName), exception.Message);
        }

        return result.Status switch
        {
            RenameCategoryStatus.Renamed => TypedResults.Ok(new CategoryResponse(
                result.Category!.Id,
                result.Category.Name,
                result.Category.CategoryType.ToString(),
                result.Category.ParentCategoryId,
                result.Category.CreatedAtUtc)),

            RenameCategoryStatus.DuplicateName => TypedResults.Problem(
                title: "Category already exists.",
                detail: "A category with this name already exists at this level.",
                statusCode: StatusCodes.Status409Conflict),

            _ => CategoryNotFound(categoryId)
        };
    }

    // 204 when deleted, 404 for a missing (or another user's) category, 409 when it has
    // subcategories or transactions reference it.
    public static async Task<Results<NoContent, ProblemHttpResult>> DeleteCategoryAsync(
        Guid categoryId,
        AuthenticatedUser user,
        DeleteCategoryHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId, categoryId, cancellationToken);

        return result switch
        {
            DeleteCategoryResult.Deleted => TypedResults.NoContent(),

            DeleteCategoryResult.HasSubcategories => TypedResults.Problem(
                title: "Category has subcategories.",
                detail: "This category can't be deleted because it has subcategories.",
                statusCode: StatusCodes.Status409Conflict),

            DeleteCategoryResult.InUse => TypedResults.Problem(
                title: "Category in use.",
                detail: "This category can't be deleted because it is used by transactions.",
                statusCode: StatusCodes.Status409Conflict),

            _ => CategoryNotFound(categoryId)
        };
    }

    private static ProblemHttpResult CategoryNotFound(Guid categoryId) =>
        TypedResults.Problem(
            title: "Category not found.",
            detail: $"Category '{categoryId}' does not exist.",
            statusCode: StatusCodes.Status404NotFound);

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
