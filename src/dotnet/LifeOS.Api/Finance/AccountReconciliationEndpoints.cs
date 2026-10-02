using LifeOS.Api.Authentication;
using LifeOS.Application.Finance.Accounts.ReconcileAccount;
using LifeOS.Contracts.Finance.Accounts;

namespace LifeOS.Api.Finance;

public static class AccountReconciliationEndpoints
{
    public static IEndpointRouteBuilder MapAccountReconciliationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/accounts/{accountId:guid}/reconciliations", ReconcileAsync)
            .RequireAuthorization().WithName("ReconcileAccountBalance");
        return endpoints;
    }

    private static async Task<IResult> ReconcileAsync(Guid accountId, ReconcileAccountRequest request,
        HttpRequest httpRequest, AuthenticatedUser user, ReconcileAccountHandler handler, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(httpRequest.Headers["Idempotency-Key"].ToString(), out var requestId) || requestId == Guid.Empty)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["idempotencyKey"] = ["A UUID Idempotency-Key header is required."] });
        var result = await handler.HandleAsync(user.UserId, new(accountId, requestId, request.ObservedBalance, request.Note), cancellationToken);
        if (result.Status == ReconcileAccountStatus.Invalid)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [result.Field ?? "request"] = [result.Message!] });
        if (result.Status != ReconcileAccountStatus.Ok)
            return TypedResults.Problem(title: result.Status == ReconcileAccountStatus.NotFound ? "Account not found." : "Reconciliation unavailable.",
                detail: result.Message, statusCode: result.Status == ReconcileAccountStatus.NotFound ? 404 : 409);
        var receipt = result.Receipt!;
        return TypedResults.Ok(new ReconcileAccountResponse(receipt.AccountId, result.Currency!, receipt.PreviousBalance,
            receipt.ObservedBalance, receipt.AdjustmentAmount, receipt.ObservedBalance, receipt.EffectiveAtUtc,
            receipt.AdjustmentAmount == 0 ? null : receipt.Id));
    }
}
