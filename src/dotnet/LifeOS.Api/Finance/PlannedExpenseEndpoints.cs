using LifeOS.Api.Authentication;
using LifeOS.Application.Finance.PlannedExpenses;
using LifeOS.Contracts.Finance.PlannedExpenses;

namespace LifeOS.Api.Finance;

public static class PlannedExpenseEndpoints
{
    public static IEndpointRouteBuilder MapPlannedExpenseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/planned-expenses").RequireAuthorization();
        g.MapGet("", async (DateOnly from, DateOnly to, int utcOffsetMinutes, AuthenticatedUser user, PlannedExpenseHandler handler, CancellationToken ct) =>
        {
            var r = await handler.QueryAsync(user.UserId, from, to, utcOffsetMinutes, ct);
            return r.Status == PlannedExpenseResultStatus.Ok ? Results.Ok(r.Items.Select(Map).ToList()) : Invalid("range", r.Message!);
        });
        g.MapGet("/{id:guid}", async (Guid id, int utcOffsetMinutes, AuthenticatedUser user, PlannedExpenseHandler handler, CancellationToken ct) =>
        {
            var r = await handler.GetAsync(user.UserId, id, utcOffsetMinutes, ct);
            return r.Status switch { PlannedExpenseResultStatus.NotFound => Results.NotFound(),
                PlannedExpenseResultStatus.Invalid => Invalid("utcOffsetMinutes", r.Message!), _ => Results.Ok(Map(r.Items.Single())) };
        });
        g.MapPost("", async (SavePlannedExpenseRequest r, AuthenticatedUser user, PlannedExpenseHandler h, CancellationToken ct) =>
            SaveResult(await h.SaveAsync(user.UserId, null, Input(r), ct)));
        g.MapPut("/{id:guid}", async (Guid id, SavePlannedExpenseRequest r, AuthenticatedUser user, PlannedExpenseHandler h, CancellationToken ct) =>
            SaveResult(await h.SaveAsync(user.UserId, id, Input(r), ct)));
        g.MapDelete("/{id:guid}", async (Guid id, AuthenticatedUser user, PlannedExpenseHandler h, CancellationToken ct) => Result(await h.DeleteAsync(user.UserId, id, ct)));
        g.MapPost("/{id:guid}/confirm", async (Guid id, int utcOffsetMinutes, ConfirmPlannedExpenseRequest r, AuthenticatedUser user, PlannedExpenseHandler h, CancellationToken ct) =>
            Result(await h.ActAsync(user.UserId, id, utcOffsetMinutes, "confirm", new(r.Amount, r.Note, r.OccurredAtUtc), ct)));
        foreach (var action in new[] { "cancel", "restore" })
            g.MapPost("/{id:guid}/" + action, async (Guid id, int utcOffsetMinutes, AuthenticatedUser user, PlannedExpenseHandler h, CancellationToken ct) =>
                Result(await h.ActAsync(user.UserId, id, utcOffsetMinutes, action, null, ct)));
        return endpoints;
    }
    private static SavePlannedExpense Input(SavePlannedExpenseRequest r) => new(r.Name, r.AccountId, r.CategoryId, r.ExpectedAmount, r.ScheduledDate, r.Note);
    private static PlannedExpenseResponse Map(PlannedExpenseSummary i) => new(i.Id, i.Name, i.AccountId, i.CategoryId, i.Currency,
        i.ExpectedAmount, i.ScheduledDate, i.Note, i.Status.ToString(), i.TransactionId, i.CreatedAtUtc, i.UpdatedAtUtc);
    private static IResult SaveResult(PlannedExpenseResult r) => r.Status == PlannedExpenseResultStatus.Ok ? Results.Ok(new PlannedExpenseSavedResponse(r.Item!.Id)) : Result(r);
    private static IResult Result(PlannedExpenseResult r) => r.Status switch
    {
        PlannedExpenseResultStatus.NotFound => Results.NotFound(),
        PlannedExpenseResultStatus.Invalid => Invalid(r.Field ?? "request", r.Message!),
        PlannedExpenseResultStatus.Conflict => Results.Problem(statusCode: 409, title: "Planned expense conflict", detail: r.Message),
        _ => Results.Ok(new PlannedExpenseActionResponse(r.Transaction?.Id ?? r.State?.TransactionId))
    };
    private static IResult Invalid(string field, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
