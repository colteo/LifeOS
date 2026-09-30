namespace LifeOS.Application.Finance.Accounts.GetAccountBalances;

// AtUtc: the instant of the balances (before anything occurring at it); null means now.
public sealed record GetAccountBalancesQuery(DateTimeOffset? AtUtc);
