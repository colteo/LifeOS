using System.Globalization;
using LifeOS.Api.Authentication;
using LifeOS.Application.WeeklyReviews;
using LifeOS.Contracts.WeeklyReviews;
using LifeOS.Domain.WeeklyReviews;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.WeeklyReviews;

// AUTO-002: the user's saved weekly reviews (read-only) and the module-owned enabled setting.
// Transport only. The owner comes only from the access token; another user's review is a 404.
// No automation/execution metadata is exposed.
public static class WeeklyReviewEndpoints
{
    public static IEndpointRouteBuilder MapWeeklyReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var reviews = endpoints.MapGroup("/api/weekly-reviews").RequireAuthorization();

        reviews.MapGet("/", GetPageAsync).WithName("GetWeeklyReviews");
        reviews.MapGet("/{reviewId:guid}", GetAsync).WithName("GetWeeklyReview");
        reviews.MapGet("/settings", GetSettingsAsync).WithName("GetWeeklyReviewSettings");
        reviews.MapPut("/settings", SetSettingsAsync).WithName("SetWeeklyReviewSettings");

        return endpoints;
    }

    // limit: 1–50 (default 20). cursor: the previous page's NextCursor.
    public static async Task<Results<Ok<WeeklyReviewPageResponse>, ValidationProblem>> GetPageAsync(
        AuthenticatedUser user,
        GetWeeklyReviewsHandler handler,
        CancellationToken cancellationToken,
        int? limit = null,
        string? cursor = null)
    {
        DateOnly? before = null;

        if (cursor is not null)
        {
            if (!TryParseCursor(cursor, out var parsed))
            {
                return Invalid("cursor", "The cursor is not valid. Start again from the first page.");
            }

            before = parsed;
        }

        WeeklyReviewPage page;

        try
        {
            page = await handler.HandleAsync(user.UserId, before, limit ?? GetWeeklyReviewsHandler.DefaultPageSize, cancellationToken);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Invalid("limit", $"The page size must be between 1 and {GetWeeklyReviewsHandler.MaxPageSize}.");
        }

        return TypedResults.Ok(new WeeklyReviewPageResponse(
            page.Items
                .Select(item => new WeeklyReviewListItemResponse(item.Id, item.WeekStartDate, item.WeekEndDate, item.GeneratedAtUtc))
                .ToList(),
            page.Next is { } next ? FormatCursor(next) : null));
    }

    public static async Task<Results<Ok<WeeklyReviewResponse>, ProblemHttpResult>> GetAsync(
        Guid reviewId,
        AuthenticatedUser user,
        GetWeeklyReviewHandler handler,
        CancellationToken cancellationToken) =>
        await handler.HandleAsync(user.UserId, reviewId, cancellationToken) is { } review
            ? TypedResults.Ok(ToResponse(review))
            : TypedResults.Problem(
                title: "Weekly review not found.",
                detail: "This weekly review does not exist.",
                statusCode: StatusCodes.Status404NotFound);

    public static async Task<Ok<WeeklyReviewSettingsResponse>> GetSettingsAsync(
        AuthenticatedUser user,
        GetWeeklyReviewSettingsHandler handler,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(new WeeklyReviewSettingsResponse(await handler.HandleAsync(user.UserId, cancellationToken)));

    public static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetSettingsAsync(
        SetWeeklyReviewSettingsRequest request,
        AuthenticatedUser user,
        SetWeeklyReviewSettingsHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.Enabled is not { } enabled)
        {
            return Invalid("enabled", "Enabled is required.");
        }

        return await handler.HandleAsync(user.UserId, enabled, cancellationToken)
            ? TypedResults.NoContent()
            : TypedResults.Problem(title: "User not found.", statusCode: StatusCodes.Status404NotFound);
    }

    internal static WeeklyReviewResponse ToResponse(WeeklyReview review)
    {
        var snapshot = review.Snapshot;

        return new WeeklyReviewResponse(
            review.Id,
            review.WeekStartDate,
            review.WeekEndDate,
            review.TimeZoneId,
            review.GeneratedAtUtc,
            review.DataVersion,
            new WeeklyFinanceResponse(snapshot.Finance.Currencies
                .Select(currency => new WeeklyCurrencyResponse(
                    currency.Currency,
                    currency.Expenses,
                    currency.Income,
                    currency.NetFlow,
                    currency.ExpenseCategories.Select(category => new WeeklyExpenseCategoryResponse(category.Name, category.Amount)).ToList()))
                .ToList()),
            new WeeklyGymResponse(
                snapshot.Gym.CompletedWorkouts,
                snapshot.Gym.TotalDurationSeconds,
                snapshot.Gym.CompletedSets,
                snapshot.Gym.PrescribedSets,
                snapshot.Gym.Workouts
                    .Select(workout => new WeeklyWorkoutResponse(
                        workout.Date, workout.WorkoutName, workout.ProgramName, workout.DurationSeconds, workout.CompletedSets, workout.PrescribedSets))
                    .ToList()),
            new WeeklyNutritionResponse(
                snapshot.Nutrition.DaysWithMeals,
                snapshot.Nutrition.MealCount,
                snapshot.Nutrition.AnalyzedMealCount,
                snapshot.Nutrition.FullyAnalyzedDays,
                snapshot.Nutrition.AnalyzedCaloriesKcal,
                snapshot.Nutrition.AnalyzedProteinGrams,
                snapshot.Nutrition.AnalyzedCarbsGrams,
                snapshot.Nutrition.AnalyzedFatGrams,
                snapshot.Nutrition.Days
                    .Select(day => new WeeklyNutritionDayResponse(
                        day.Date, day.MealCount, day.AnalyzedMealCount, day.CaloriesKcal, day.ProteinGrams, day.CarbsGrams, day.FatGrams))
                    .ToList()));
    }

    // ---- Cursor ----

    // The last item's week-ending date, "yyyy-MM-dd" (unique per user).
    internal static string FormatCursor(DateOnly weekEndDate) => weekEndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static bool TryParseCursor(string text, out DateOnly weekEndDate) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out weekEndDate);

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
