using System.Globalization;
using LifeOS.Api.Authentication;
using LifeOS.Application.WeeklyReviews;
using LifeOS.Contracts.WeeklyReviews;
using LifeOS.Domain.WeeklyReviews;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.WeeklyReviews;

// AUTO-002: the user's saved weekly reviews (read-only) and the module-owned enabled setting.
// AI-001: the review's AI Insights (read, and generate on demand).
// Transport only. The owner comes only from the access token; another user's review is a 404.
// No automation/execution metadata is exposed.
public static class WeeklyReviewEndpoints
{
    public const string InsightsUnavailableMessage = "AI insights are unavailable right now. Try again later.";
    public const string InsightsFailedMessage = "AI insights could not be generated for this review. Try again later.";

    public static IEndpointRouteBuilder MapWeeklyReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var reviews = endpoints.MapGroup("/api/weekly-reviews").RequireAuthorization();

        reviews.MapGet("/", GetPageAsync).WithName("GetWeeklyReviews");
        reviews.MapGet("/{reviewId:guid}", GetAsync).WithName("GetWeeklyReview");
        reviews.MapGet("/settings", GetSettingsAsync).WithName("GetWeeklyReviewSettings");
        reviews.MapPut("/settings", SetSettingsAsync).WithName("SetWeeklyReviewSettings");
        reviews.MapGet("/{reviewId:guid}/insights", GetInsightsAsync).WithName("GetWeeklyReviewInsights");
        reviews.MapPost("/{reviewId:guid}/insights", GenerateInsightsAsync).WithName("GenerateWeeklyReviewInsights");

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

    // Never calls the AI service: the stored insights, or NotGenerated.
    public static async Task<Results<Ok<WeeklyReviewInsightsStateResponse>, ProblemHttpResult>> GetInsightsAsync(
        Guid reviewId,
        AuthenticatedUser user,
        GetWeeklyReviewInsightsHandler handler,
        CancellationToken cancellationToken) =>
        ToInsightsResult(await handler.HandleAsync(user.UserId, reviewId, cancellationToken));

    // Generates the insights once; afterwards returns the stored ones without an AI call. A failure
    // stores nothing and changes nothing: the review stays as it is and the request can be retried.
    public static async Task<Results<Ok<WeeklyReviewInsightsStateResponse>, ProblemHttpResult>> GenerateInsightsAsync(
        Guid reviewId,
        AuthenticatedUser user,
        GenerateWeeklyReviewInsightsHandler handler,
        CancellationToken cancellationToken) =>
        ToInsightsResult(await handler.HandleAsync(user.UserId, reviewId, cancellationToken));

    private static Results<Ok<WeeklyReviewInsightsStateResponse>, ProblemHttpResult> ToInsightsResult(WeeklyReviewInsightsResult result) =>
        result.Status switch
        {
            WeeklyReviewInsightsStatus.Available => TypedResults.Ok(new WeeklyReviewInsightsStateResponse(
                WeeklyReviewInsightsStatuses.Available, ToResponse(result.Insights!))),
            WeeklyReviewInsightsStatus.NotGenerated => TypedResults.Ok(new WeeklyReviewInsightsStateResponse(
                WeeklyReviewInsightsStatuses.NotGenerated, null)),
            WeeklyReviewInsightsStatus.NotFound => TypedResults.Problem(
                title: "Weekly review not found.",
                detail: "This weekly review does not exist.",
                statusCode: StatusCodes.Status404NotFound),
            WeeklyReviewInsightsStatus.Failed => TypedResults.Problem(
                title: "AI insights not generated",
                detail: InsightsFailedMessage,
                statusCode: StatusCodes.Status502BadGateway),
            _ => TypedResults.Problem(
                title: "AI insights unavailable",
                detail: InsightsUnavailableMessage,
                statusCode: StatusCodes.Status503ServiceUnavailable)
        };

    internal static WeeklyReviewInsightsResponse ToResponse(WeeklyReviewInsights insights) => new(
        insights.Content.Summary,
        insights.Content.Wins,
        insights.Content.Attention,
        insights.Content.Patterns,
        insights.Content.NextWeekFocus,
        insights.GeneratedAtUtc,
        insights.OutputVersion,
        insights.Generation.Provider,
        insights.Generation.Model,
        insights.Generation.PromptVersion);

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
