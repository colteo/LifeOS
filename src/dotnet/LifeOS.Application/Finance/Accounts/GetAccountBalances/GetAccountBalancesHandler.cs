using LifeOS.Application.Finance.Accounts.SetOpeningBalance;
using LifeOS.Application.Finance.Transactions;
using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.Application.Finance.Accounts.GetAccountBalances;

// Derived balances of all the caller's accounts at one instant (ADR-007). Nothing is stored:
// the Domain calculator applies each account's opening balance and the caller's transactions.
public sealed class GetAccountBalancesHandler
{
    private readonly IAccountRepository _accountRepository;
    private readonly IOpeningBalanceRepository _openingBalanceRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly TimeProvider _timeProvider;

    public GetAccountBalancesHandler(
        IAccountRepository accountRepository,
        IOpeningBalanceRepository openingBalanceRepository,
        ITransactionRepository transactionRepository,
        TimeProvider timeProvider)
    {
        _accountRepository = accountRepository;
        _openingBalanceRepository = openingBalanceRepository;
        _transactionRepository = transactionRepository;
        _timeProvider = timeProvider;
    }

    public async Task<GetAccountBalancesResult> HandleAsync(
        Guid userId,
        GetAccountBalancesQuery query,
        CancellationToken cancellationToken)
    {
        if (query.AtUtc is { Offset: var offset } && offset != TimeSpan.Zero)
        {
            return GetAccountBalancesResult.Invalid("atUtc", "atUtc must be a UTC value.");
        }

        var atUtc = query.AtUtc ?? _timeProvider.GetUtcNow();

        var accounts = await _accountRepository.GetAllAsync(userId, cancellationToken);
        var openingBalances = (await _openingBalanceRepository.GetAllAsync(userId, cancellationToken))
            .ToDictionary(openingBalance => openingBalance.AccountId);

        // v1: every transaction of the caller before atUtc, in one scoped query.
        var transactions = await _transactionRepository.GetOccurredBeforeAsync(userId, atUtc, cancellationToken);

        return GetAccountBalancesResult.Ok(accounts
            .OrderBy(account => account.CreatedAtUtc)
            .ThenBy(account => account.Id)
            .Select(account =>
            {
                var openingBalance = openingBalances.GetValueOrDefault(account.Id);

                return new AccountBalance(
                    account.Id,
                    account.Currency,
                    AccountBalanceCalculator.Calculate(account, openingBalance, transactions, atUtc),
                    atUtc,
                    openingBalance is null
                        ? null
                        : new OpeningBalanceSummary(account.Id, openingBalance.Amount, openingBalance.AsOfUtc));
            })
            .ToList());
    }
}
