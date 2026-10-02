using LifeOS.Api.Authentication;
using LifeOS.Application.Finance.Recurring;
using LifeOS.Contracts.Finance.Recurring;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Api.Finance;

public static class RecurringEndpoints
{
    public static IEndpointRouteBuilder MapRecurringEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/recurring").RequireAuthorization();
        g.MapGet("", async (int fromYear, int fromMonth, int toYear, int toMonth, int utcOffsetMinutes,
            AuthenticatedUser user, RecurringHandler handler, CancellationToken ct) =>
        {
            var r = await handler.QueryAsync(user.UserId, fromYear, fromMonth, toYear, toMonth, utcOffsetMinutes, ct);
            if (r.Status != RecurringResultStatus.Ok) return Invalid("range", r.Message!);
            return Results.Ok(new RecurringResponse(r.Rules.Select(Rule).ToList(), r.Occurrences.Select(o =>
                new RecurringOccurrenceResponse(o.RuleId, o.Name, o.Type.ToString(), o.AccountId, o.CategoryId,
                    o.Currency, o.ExpectedAmount, o.Note, o.Year, o.Month, o.ScheduledDate, o.Status.ToString(), o.TransactionId)).ToList()));
        });
        g.MapPost("", async (SaveRecurringRuleRequest request, AuthenticatedUser user, RecurringHandler handler, CancellationToken ct) =>
            await Save(null, request, user, handler, ct));
        g.MapPut("/{id:guid}", async (Guid id, SaveRecurringRuleRequest request, AuthenticatedUser user, RecurringHandler handler, CancellationToken ct) =>
            await Save(id, request, user, handler, ct));
        g.MapDelete("/{id:guid}", async (Guid id, AuthenticatedUser user, RecurringHandler handler, CancellationToken ct) =>
            Result(await handler.DeleteAsync(user.UserId, id, ct)));
        g.MapPost("/{id:guid}/{year:int}/{month:int}/confirm", async (Guid id, int year, int month, int utcOffsetMinutes,
            ConfirmRecurringRequest request, AuthenticatedUser user, RecurringHandler handler, CancellationToken ct) =>
            Result(await handler.ActAsync(user.UserId, id, year, month, utcOffsetMinutes, "confirm", new(request.Amount, request.Note, request.OccurredAtUtc), ct)));
        g.MapPost("/{id:guid}/{year:int}/{month:int}/skip", async (Guid id, int year, int month, int utcOffsetMinutes,
            AuthenticatedUser user, RecurringHandler handler, CancellationToken ct) =>
            Result(await handler.ActAsync(user.UserId, id, year, month, utcOffsetMinutes, "skip", null, ct)));
        g.MapPost("/{id:guid}/{year:int}/{month:int}/restore", async (Guid id, int year, int month, int utcOffsetMinutes,
            AuthenticatedUser user, RecurringHandler handler, CancellationToken ct) =>
            Result(await handler.ActAsync(user.UserId, id, year, month, utcOffsetMinutes, "restore", null, ct)));
        return endpoints;
    }

    private static async Task<IResult> Save(Guid? id, SaveRecurringRuleRequest r, AuthenticatedUser user, RecurringHandler handler, CancellationToken ct)
    {
        if (r.Type is not ("Income" or "Expense") || !Enum.TryParse<TransactionType>(r.Type, false, out var type))
            return Invalid("type", "Type must be Income or Expense.");
        return Result(await handler.SaveAsync(user.UserId, id, new(r.Name, type, r.AccountId, r.CategoryId,
            r.Amount, r.DayOfMonth, r.StartYear, r.StartMonth, r.Note, r.EndYear, r.EndMonth), ct));
    }
    private static RecurringRuleResponse Rule(LifeOS.Domain.Finance.Recurring.RecurringTransactionRule r) =>
        new(r.Id, r.Name, r.TransactionType.ToString(), r.AccountId, r.CategoryId, r.Amount, r.DayOfMonth, r.StartYear, r.StartMonth, r.Note, r.EndYear, r.EndMonth);
    private static IResult Result(RecurringResult r) => r.Status switch
    {
        RecurringResultStatus.NotFound => Results.NotFound(),
        RecurringResultStatus.Invalid => Invalid(r.Field ?? "request", r.Message!),
        RecurringResultStatus.Conflict => Results.Problem(statusCode: 409, title: "Recurring action conflict", detail: r.Message),
        _ when r.Rule is not null && r.Change == RecurringChange.SaveRule => Results.Ok(Rule(r.Rule)),
        _ => Results.Ok(new RecurringActionResponse(r.Transaction?.Id ?? r.State?.TransactionId))
    };
    private static IResult Invalid(string field, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
