using LifeOS.Domain.Finance.Accounts;

namespace LifeOS.Application.Finance.Accounts.SetOpeningBalance;

// Gives an existing account its opening balance (ADR-007). Create-only in v1: repeating the same
// values succeeds without writing; different values are a conflict.
public sealed class SetOpeningBalanceHandler
{
    private readonly IAccountRepository _accountRepository;
    private readonly IOpeningBalanceRepository _openingBalanceRepository;
    private readonly TimeProvider _timeProvider;

    public SetOpeningBalanceHandler(
        IAccountRepository accountRepository,
        IOpeningBalanceRepository openingBalanceRepository,
        TimeProvider timeProvider)
    {
        _accountRepository = accountRepository;
        _openingBalanceRepository = openingBalanceRepository;
        _timeProvider = timeProvider;
    }

    // Throws ArgumentException for an invalid amount or a future instant.
    public async Task<SetOpeningBalanceResult> HandleAsync(
        Guid userId,
        SetOpeningBalanceCommand command,
        CancellationToken cancellationToken)
    {
        // Scoped: another user's account is reported exactly like a missing one.
        var account = await _accountRepository.GetByIdAsync(userId, command.AccountId, cancellationToken);

        if (account is null)
        {
            return SetOpeningBalanceResult.NotFound();
        }

        var existing = await _openingBalanceRepository.GetByAccountIdAsync(userId, account.Id, cancellationToken);

        if (existing is not null)
        {
            return Resolve(existing, command);
        }

        var openingBalance = OpeningBalance.Create(account, command.Amount, command.AsOfUtc, _timeProvider.GetUtcNow());

        if (await _openingBalanceRepository.TryAddAsync(openingBalance, cancellationToken))
        {
            return SetOpeningBalanceResult.Created(ToSummary(openingBalance));
        }

        // Not stored: a concurrent request created one first (apply the same retry rule to what it
        // stored), or the account was deleted concurrently. One re-read of each decides.
        var winner = await _openingBalanceRepository.GetByAccountIdAsync(userId, account.Id, cancellationToken);

        if (winner is not null)
        {
            return Resolve(winner, command);
        }

        return await _accountRepository.GetByIdAsync(userId, account.Id, cancellationToken) is null
            ? SetOpeningBalanceResult.NotFound()
            : SetOpeningBalanceResult.Conflict();
    }

    private static SetOpeningBalanceResult Resolve(OpeningBalance existing, SetOpeningBalanceCommand command) =>
        existing.Amount == command.Amount && existing.AsOfUtc == OpeningBalance.NormalizeAsOf(command.AsOfUtc)
            ? SetOpeningBalanceResult.Unchanged(ToSummary(existing))
            : SetOpeningBalanceResult.Conflict();

    private static OpeningBalanceSummary ToSummary(OpeningBalance openingBalance) =>
        new(openingBalance.AccountId, openingBalance.Amount, openingBalance.AsOfUtc);
}
