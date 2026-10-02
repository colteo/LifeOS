using LifeOS.Api.Authentication;
using LifeOS.Application.Finance.Budgets;
using LifeOS.Contracts.Finance.Budgets;

namespace LifeOS.Api.Finance;

public static class BudgetEndpoints
{
    public static IEndpointRouteBuilder MapBudgetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/budgets/{year:int}/{month:int}/{currency}").RequireAuthorization();
        group.MapGet("", GetAsync).WithName("GetMonthlyBudget");
        group.MapPut("", SetAsync).WithName("SetMonthlyBudget");
        group.MapDelete("", DeleteAsync).WithName("DeleteMonthlyBudget");
        return endpoints;
    }

    private static async Task<IResult> GetAsync(int year, int month, string currency, string? fromUtc, string? toUtc,
        int utcOffsetMinutes, AuthenticatedUser user, GetMonthlyBudgetHandler handler, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (TransactionEndpoints.TryParseUtcInstant("fromUtc", fromUtc, out var from) is { } fromError)
            errors["fromUtc"] = [fromError];
        if (TransactionEndpoints.TryParseUtcInstant("toUtc", toUtc, out var to) is { } toError)
            errors["toUtc"] = [toError];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
        var result = await handler.HandleAsync(user.UserId, new(year, month, currency, from, to, utcOffsetMinutes), cancellationToken);
        if (result.Status == MonthlyBudgetStatus.Invalid) return Invalid(result);
        var b = result.Budget;
        return TypedResults.Ok(new GetMonthlyBudgetResponse(b is null ? null :
            new(b.Id, b.Year, b.Month, b.Currency, b.Amount, b.Spent, b.Remaining, b.RemainingDays, b.SafeDailySpend, b.ExpectedRecurringExpenses, b.FreeToSpend)));
    }

    private static async Task<IResult> SetAsync(int year, int month, string currency, SetMonthlyBudgetRequest request,
        AuthenticatedUser user, SetMonthlyBudgetHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId, new(year, month, currency, request.Amount), cancellationToken);
        return result.Status == MonthlyBudgetStatus.Invalid ? Invalid(result) : TypedResults.NoContent();
    }

    private static async Task<IResult> DeleteAsync(int year, int month, string currency,
        AuthenticatedUser user, DeleteMonthlyBudgetHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(user.UserId, year, month, currency, cancellationToken);
        return result.Status == MonthlyBudgetStatus.Invalid ? Invalid(result) : TypedResults.NoContent();
    }

    private static IResult Invalid(MonthlyBudgetResult result) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [result.Field!] = [result.Message!] });
}
