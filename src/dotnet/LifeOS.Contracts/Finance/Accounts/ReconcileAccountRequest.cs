namespace LifeOS.Contracts.Finance.Accounts;

public sealed record ReconcileAccountRequest(decimal ObservedBalance, string? Note);
public sealed record ReconcileAccountResponse(Guid AccountId, string Currency, decimal PreviousBalance,
    decimal ObservedBalance, decimal AdjustmentAmount, decimal ResultingBalance, DateTimeOffset EffectiveAtUtc,
    Guid? AdjustmentId);
