using System.Globalization;
using LifeOS.Api.Authentication;
using LifeOS.Application.Nutrition;
using LifeOS.Contracts.Nutrition;
using LifeOS.Domain.Nutrition;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Nutrition;

// NUT-002: on-demand AI nutrition estimation. Creating or editing a meal never calls these; they run
// only on an explicit request (estimate one meal, analyze a day) or on Home's lazy close of past days.
public static class NutritionAnalysisEndpoints
{
    public const string UnavailableMessage = "Nutrition estimation is unavailable right now. Try again later.";
    public const string NotEstimableMessage = "Could not estimate this meal. Try again, or enter the values yourself.";

    public static IEndpointRouteBuilder MapNutritionAnalysisEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var nutrition = endpoints.MapGroup("/api/nutrition")
            .RequireAuthorization();

        nutrition.MapGet("/summary", GetSummaryAsync).WithName("GetNutritionSummary");
        nutrition.MapPost("/meals/{id:guid}/estimate", EstimateMealAsync).WithName("EstimateMealNutrition");
        nutrition.MapPut("/meals/{id:guid}/nutrition", SetMealNutritionAsync).WithName("SetMealNutrition");
        nutrition.MapPost("/analyze", AnalyzeDayAsync).WithName("AnalyzeNutritionDay");
        nutrition.MapPost("/lazy-close", LazyCloseAsync).WithName("LazyCloseNutrition");

        return endpoints;
    }

    // ?date=yyyy-MM-dd: deterministic totals of that diary day's current snapshots.
    public static async Task<Results<Ok<DailyNutritionSummaryResponse>, ValidationProblem>> GetSummaryAsync(
        string? date,
        AuthenticatedUser user,
        GetDailyNutritionSummaryHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseDate(date, out var day))
        {
            return NutritionEndpoints.Invalid("date", "Supply the diary date as yyyy-MM-dd.");
        }

        var result = await handler.HandleAsync(user.UserId, day, cancellationToken);

        return result.Status == NutritionStatus.Ok
            ? TypedResults.Ok(ToResponse(result.Summary!))
            : NutritionEndpoints.Invalid("date", result.Message!);
    }

    // A proposal for one meal; nothing is stored.
    public static async Task<Results<Ok<NutritionEstimateResponse>, NotFound, ProblemHttpResult>> EstimateMealAsync(
        Guid id,
        AuthenticatedUser user,
        EstimateMealNutritionHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId, id, cancellationToken);

        return result.Status switch
        {
            NutritionStatus.NotFound => TypedResults.NotFound(),
            NutritionStatus.Ok => TypedResults.Ok(new NutritionEstimateResponse(result.Proposal!.Values.CaloriesKcal,
                result.Proposal.Values.ProteinGrams, result.Proposal.Values.CarbsGrams, result.Proposal.Values.FatGrams,
                result.Proposal.Assumptions)),
            NutritionStatus.NotEstimable => TypedResults.Problem(NotEstimableMessage, statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Meal not estimated"),
            _ => Unavailable()
        };
    }

    // Confirm (AiConfirmed) or edit (UserAdjusted): replaces the meal's current nutrition.
    public static async Task<Results<Ok<MealResponse>, ValidationProblem, NotFound>> SetMealNutritionAsync(
        Guid id,
        SetMealNutritionRequest request,
        AuthenticatedUser user,
        SetMealNutritionHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.CaloriesKcal is not { } calories)
        {
            return NutritionEndpoints.Invalid("caloriesKcal", "Calories are required.");
        }

        if (request.ProteinGrams is not { } protein)
        {
            return NutritionEndpoints.Invalid("proteinGrams", "Protein is required.");
        }

        if (request.CarbsGrams is not { } carbs)
        {
            return NutritionEndpoints.Invalid("carbsGrams", "Carbohydrates are required.");
        }

        if (request.FatGrams is not { } fat)
        {
            return NutritionEndpoints.Invalid("fatGrams", "Fat is required.");
        }

        if (!TryParseExplicitSource(request.Source, out var source))
        {
            return NutritionEndpoints.Invalid("source", "Source must be AiConfirmed or UserAdjusted.");
        }

        var result = await handler.HandleAsync(user.UserId, id, new SetMealNutritionCommand(calories, protein, carbs, fat, source),
            cancellationToken);

        return result.Status switch
        {
            MealResultStatus.NotFound => TypedResults.NotFound(),
            MealResultStatus.Invalid => NutritionEndpoints.Invalid(result),
            _ => TypedResults.Ok(NutritionEndpoints.ToResponse(result.Meal!))
        };
    }

    // ?date=yyyy-MM-dd: estimates that day's meals without nutrition. Partial success is 200.
    public static async Task<Results<Ok<NutritionAnalysisResponse>, ValidationProblem>> AnalyzeDayAsync(
        string? date,
        AuthenticatedUser user,
        AnalyzeDayHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryParseDate(date, out var day))
        {
            return NutritionEndpoints.Invalid("date", "Supply the diary date as yyyy-MM-dd.");
        }

        var result = await handler.HandleAsync(user.UserId, day, cancellationToken);

        return result.Status == NutritionStatus.Ok
            ? TypedResults.Ok(ToResponse(result.Analysis!, result.Summary))
            : NutritionEndpoints.Invalid("date", result.Message!);
    }

    // Estimates a bounded number of past meals without nutrition. Never today's meals.
    public static async Task<Results<Ok<NutritionAnalysisResponse>, ValidationProblem>> LazyCloseAsync(
        LazyCloseRequest request,
        AuthenticatedUser user,
        LazyCloseNutritionHandler handler,
        CancellationToken cancellationToken)
    {
        if (request.UtcOffsetMinutes is not { } offset)
        {
            return NutritionEndpoints.Invalid("utcOffsetMinutes", "UTC offset is required.");
        }

        var result = await handler.HandleAsync(user.UserId, offset, cancellationToken);

        return result.Status == NutritionStatus.Ok
            ? TypedResults.Ok(ToResponse(result.Analysis!, null))
            : NutritionEndpoints.Invalid(result.Field!, result.Message!);
    }

    private static ProblemHttpResult Unavailable() =>
        TypedResults.Problem(UnavailableMessage, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Nutrition estimation unavailable");

    private static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    // Only the two explicit sources are accepted from clients; names only, ignoring case.
    private static bool TryParseExplicitSource(string? value, out NutritionSource source)
    {
        source = default;
        var name = new[] { NutritionSource.AiConfirmed, NutritionSource.UserAdjusted }
            .Select(candidate => (NutritionSource?)candidate)
            .FirstOrDefault(candidate => string.Equals(candidate.ToString(), value?.Trim(), StringComparison.OrdinalIgnoreCase));

        if (name is not { } parsed)
        {
            return false;
        }

        source = parsed;

        return true;
    }

    private static DailyNutritionSummaryResponse ToResponse(DailyNutritionSummary summary) => new(summary.Date, summary.MealCount,
        summary.AnalyzedMealCount, summary.AllAnalyzed, summary.CaloriesKcal, summary.ProteinGrams, summary.CarbsGrams, summary.FatGrams,
        NutritionTargetEndpoints.ToDto(summary.Target));

    private static NutritionAnalysisResponse ToResponse(NutritionAnalysisResult analysis, DailyNutritionSummary? summary) =>
        new(analysis.Analyzed, analysis.Failed, analysis.EstimationUnavailable, analysis.MorePending,
            summary is null ? null : ToResponse(summary));
}
