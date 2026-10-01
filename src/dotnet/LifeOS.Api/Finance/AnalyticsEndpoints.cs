using LifeOS.Api.Authentication;
using LifeOS.Application.Finance.Analytics;
using LifeOS.Application.Finance.Analytics.GetMonthlyAnalytics;
using LifeOS.Contracts.Finance.Analytics;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LifeOS.Api.Finance;

public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Analytics cover only the authenticated user's data; the user id comes only from the access token.
        var analytics = endpoints.MapGroup("/api/analytics")
            .RequireAuthorization();

        analytics.MapGet("/monthly", GetMonthlyAnalyticsAsync)
            .WithName("GetMonthlyAnalytics");

        return endpoints;
    }

    // fromUtc/toUtc: the caller's local calendar month as a half-open UTC range (at most 32 days).
    // Query values are parsed here so malformed input returns a ValidationProblem.
    public static async Task<Results<Ok<MonthlyAnalyticsResponse>, ValidationProblem>> GetMonthlyAnalyticsAsync(
        string? fromUtc,
        string? toUtc,
        AuthenticatedUser user,
        GetMonthlyAnalyticsHandler handler,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (TransactionEndpoints.TryParseUtcInstant("fromUtc", fromUtc, out var from) is { } fromError)
        {
            errors["fromUtc"] = [fromError];
        }

        if (TransactionEndpoints.TryParseUtcInstant("toUtc", toUtc, out var to) is { } toError)
        {
            errors["toUtc"] = [toError];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var result = await handler.HandleAsync(user.UserId, new GetMonthlyAnalyticsQuery(from, to), cancellationToken);

        if (result.Status == GetMonthlyAnalyticsStatus.Invalid)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [result.Field!] = [result.Message!]
            });
        }

        return TypedResults.Ok(new MonthlyAnalyticsResponse(from, to, result.Currencies.Select(ToResponse).ToList()));
    }

    private static CurrencyAnalyticsResponse ToResponse(CurrencyAnalytics currency) =>
        new(
            currency.Currency,
            currency.Expenses,
            currency.Income,
            currency.NetFlow,
            currency.ExpenseCategories
                .Select(category => new ExpenseCategoryAnalyticsResponse(
                    category.CategoryId,
                    category.Name,
                    category.Amount,
                    category.DirectAmount,
                    category.Subcategories
                        .Select(subcategory => new ExpenseSubcategoryAnalyticsResponse(
                            subcategory.CategoryId,
                            subcategory.Name,
                            subcategory.Amount))
                        .ToList()))
                .ToList());
}
