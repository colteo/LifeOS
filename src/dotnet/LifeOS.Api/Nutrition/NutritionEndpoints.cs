using System.Globalization;
using LifeOS.Api.Authentication;
using LifeOS.Application.Nutrition;
using LifeOS.Contracts.Nutrition;
using LifeOS.Domain.Nutrition;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Nutrition;

public static class NutritionEndpoints
{
    public static IEndpointRouteBuilder MapNutritionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Meals are owned by the authenticated user; the user id comes only from the access token.
        var meals = endpoints.MapGroup("/api/nutrition/meals")
            .RequireAuthorization();

        meals.MapGet("/", GetMealsAsync).WithName("GetMeals");
        meals.MapPost("/", CreateMealAsync).WithName("CreateMeal");
        meals.MapPut("/{id:guid}", UpdateMealAsync).WithName("UpdateMeal");
        meals.MapDelete("/{id:guid}", DeleteMealAsync).WithName("DeleteMeal");

        return endpoints;
    }

    // ?date=yyyy-MM-dd (required): that diary day, newest first.
    public static async Task<Results<Ok<IReadOnlyList<MealResponse>>, ValidationProblem>> GetMealsAsync(
        string? date,
        AuthenticatedUser user,
        GetMealsForDateHandler handler,
        CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var diaryDate))
        {
            return Invalid("date", "Supply the diary date as yyyy-MM-dd.");
        }

        var result = await handler.HandleAsync(user.UserId, diaryDate, cancellationToken);

        if (result.Status != MealResultStatus.Ok)
        {
            return Invalid("date", result.Message!);
        }

        IReadOnlyList<MealResponse> response = result.Meals.Select(ToResponse).ToList();

        return TypedResults.Ok(response);
    }

    public static async Task<Results<Created<MealResponse>, ValidationProblem>> CreateMealAsync(
        CreateMealRequest request,
        AuthenticatedUser user,
        CreateMealHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseMealType(request.MealType, out var mealType))
        {
            return Invalid("mealType", "Meal type must be Breakfast, Lunch, Dinner, Snack, Other or empty.");
        }

        if (request.Date is not { } date)
        {
            return Invalid("date", "Date is required.");
        }

        if (request.Time is not { } time)
        {
            return Invalid("time", "Time is required.");
        }

        if (request.UtcOffsetMinutes is not { } offset)
        {
            return Invalid("utcOffsetMinutes", "UTC offset is required.");
        }

        var result = await handler.HandleAsync(user.UserId,
            new CreateMealCommand(request.Description ?? "", mealType, date, time, offset), cancellationToken);

        if (result.Status != MealResultStatus.Ok)
        {
            return Invalid(result);
        }

        var response = ToResponse(result.Meal!);

        return TypedResults.Created($"/api/nutrition/meals/{response.Id}", response);
    }

    public static async Task<Results<Ok<MealResponse>, ValidationProblem, NotFound>> UpdateMealAsync(
        Guid id,
        UpdateMealRequest request,
        AuthenticatedUser user,
        UpdateMealHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseMealType(request.MealType, out var mealType))
        {
            return Invalid("mealType", "Meal type must be Breakfast, Lunch, Dinner, Snack, Other or empty.");
        }

        if (request.Time is not { } time)
        {
            return Invalid("time", "Time is required.");
        }

        var result = await handler.HandleAsync(user.UserId, id,
            new UpdateMealCommand(request.Description ?? "", mealType, time), cancellationToken);

        return result.Status switch
        {
            MealResultStatus.NotFound => TypedResults.NotFound(),
            MealResultStatus.Invalid => Invalid(result),
            _ => TypedResults.Ok(ToResponse(result.Meal!))
        };
    }

    public static async Task<Results<NoContent, NotFound>> DeleteMealAsync(
        Guid id,
        AuthenticatedUser user,
        DeleteMealHandler handler,
        CancellationToken cancellationToken) =>
        await handler.HandleAsync(user.UserId, id, cancellationToken) == MealResultStatus.Ok
            ? TypedResults.NoContent()
            : TypedResults.NotFound();

    // Null or blank is "no meal type". Otherwise a name, ignoring case: Enum.TryParse would also
    // accept numeric and comma-combined values.
    private static bool TryParseMealType(string? value, out MealType? mealType)
    {
        mealType = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var name = Enum.GetNames<MealType>()
            .FirstOrDefault(name => string.Equals(name, value.Trim(), StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            return false;
        }

        mealType = Enum.Parse<MealType>(name);

        return true;
    }

    private static MealResponse ToResponse(MealEntrySummary meal) => new(meal.Id, meal.Description,
        meal.MealType?.ToString(), meal.DiaryDate, meal.DiaryTime, meal.OccurredAtUtc, meal.CreatedAtUtc, meal.UpdatedAtUtc);

    // The domain names its parameters; diaryDate is the request's date.
    private static ValidationProblem Invalid(MealResult result) =>
        Invalid(result.Field == "diaryDate" ? "date" : result.Field ?? "request", result.Message!);

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
