namespace LifeOS.Application.Finance.Accounts.SetOpeningBalance;

public enum SetOpeningBalanceStatus
{
    Created,

    // The same opening balance already exists: an idempotent retry.
    Unchanged,

    NotFound,

    // A different opening balance already exists; changing it is not supported yet.
    Conflict
}

public sealed record OpeningBalanceSummary(Guid AccountId, decimal Amount, DateTimeOffset AsOfUtc);

public sealed record SetOpeningBalanceResult(SetOpeningBalanceStatus Status, OpeningBalanceSummary? OpeningBalance)
{
    public static SetOpeningBalanceResult Created(OpeningBalanceSummary openingBalance) =>
        new(SetOpeningBalanceStatus.Created, openingBalance);

    public static SetOpeningBalanceResult Unchanged(OpeningBalanceSummary openingBalance) =>
        new(SetOpeningBalanceStatus.Unchanged, openingBalance);

    public static SetOpeningBalanceResult NotFound() => new(SetOpeningBalanceStatus.NotFound, null);

    public static SetOpeningBalanceResult Conflict() => new(SetOpeningBalanceStatus.Conflict, null);
}
