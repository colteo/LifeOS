using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.Application.Finance.Accounts.ReconcileAccount;

public enum ReconcileAccountStatus { Ok, NotFound, Invalid, Unavailable, Conflict }
public sealed record ReconcileAccountResult(ReconcileAccountStatus Status, AccountReconciliation? Receipt,
    string? Currency, bool IsReplay, string? Field, string? Message)
{
    public AccountBalanceAdjustment? Adjustment => IsReplay || Receipt is null ? null : AccountBalanceAdjustment.From(Receipt);
    public static ReconcileAccountResult Ok(AccountReconciliation receipt, string currency, bool replay = false) =>
        new(ReconcileAccountStatus.Ok, receipt, currency, replay, null, null);
    public static ReconcileAccountResult Failure(ReconcileAccountStatus status, string message, string? field = null) =>
        new(status, null, null, false, field, message);
}
public sealed record ReconcileAccountCommand(Guid AccountId, Guid RequestId, decimal ObservedBalance, string? Note);

public sealed class ReconcileAccountHandler(IAccountReconciliationRepository repository, TimeProvider clock)
{
    public Task<ReconcileAccountResult> HandleAsync(Guid userId, ReconcileAccountCommand command, CancellationToken cancellationToken) =>
        repository.ExecuteAsync(userId, command.AccountId, command.RequestId, snapshot => Decide(snapshot, command), cancellationToken);

    private ReconcileAccountResult Decide(ReconciliationSnapshot snapshot, ReconcileAccountCommand command)
    {
        if (snapshot.Account is not { } account)
            return ReconcileAccountResult.Failure(ReconcileAccountStatus.NotFound, "Account not found.");
        try
        {
            if (command.RequestId == Guid.Empty)
                return ReconcileAccountResult.Failure(ReconcileAccountStatus.Invalid, "A UUID Idempotency-Key is required.", "idempotencyKey");
            AccountReconciliation.ValidateMoney(command.ObservedBalance, "observedBalance");
            var note = AccountReconciliation.NormalizeNote(command.Note);
            if (snapshot.Receipt is { } receipt)
                return receipt.ObservedBalance == command.ObservedBalance && receipt.Note == note
                    ? ReconcileAccountResult.Ok(receipt, account.Currency, true)
                    : ReconcileAccountResult.Failure(ReconcileAccountStatus.Conflict, "This request key was already used with different input.");

            var now = OpeningBalance.NormalizeAsOf(clock.GetUtcNow());
            if (snapshot.Adjustments.Any(a => a.EffectiveAtUtc > now))
                return ReconcileAccountResult.Failure(ReconcileAccountStatus.Conflict, "The server time is earlier than an existing reconciliation. Try again later.");
            var previous = AccountBalanceCalculator.Calculate(account, snapshot.OpeningBalance, snapshot.Transactions, now, snapshot.Adjustments);
            if (previous is null)
                return ReconcileAccountResult.Failure(ReconcileAccountStatus.Unavailable, "The current balance is unavailable before its opening balance baseline.");
            return ReconcileAccountResult.Ok(AccountReconciliation.Create(account, command.RequestId,
                previous.Value, command.ObservedBalance, now, note), account.Currency);
        }
        catch (ArgumentException exception)
        {
            return ReconcileAccountResult.Failure(ReconcileAccountStatus.Invalid, exception.Message, exception.ParamName);
        }
        catch (OverflowException)
        {
            return ReconcileAccountResult.Failure(ReconcileAccountStatus.Invalid, "The calculated difference exceeds the supported monetary range.", "observedBalance");
        }
    }
}
