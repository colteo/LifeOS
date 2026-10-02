using LifeOS.Application.Finance.Recurring;
using LifeOS.Domain.Finance.Recurring;

namespace LifeOS.UnitTests.Fakes;

internal sealed class InMemoryRecurringRepository(InMemoryAccountRepository accounts,
    InMemoryCategoryRepository categories, InMemoryTransactionRepository transactions) : IRecurringRepository
{
    private readonly SemaphoreSlim gate = new(1);
    public List<RecurringTransactionRule> Rules { get; } = [];
    public List<RecurringOccurrenceState> States { get; } = [];
    public Task<RecurringRead> ReadAsync(Guid owner, int fy, int fm, int ty, int tm, CancellationToken ct,
        DateTimeOffset? transactionsFromUtc = null, DateTimeOffset? transactionsToUtc = null) =>
        Task.FromResult(new RecurringRead(Rules.Where(r => r.UserId == owner).ToList(),
            States.Where(s => s.UserId == owner && s.Year * 12 + s.Month >= fy * 12 + fm && s.Year * 12 + s.Month <= ty * 12 + tm).ToList(),
            accounts.Accounts.Where(a => a.UserId == owner).ToList(),
            transactionsFromUtc is not null && transactionsToUtc is not null ? transactions.Transactions.Where(t => t.UserId == owner
                && t.OccurredAtUtc >= transactionsFromUtc && t.OccurredAtUtc < transactionsToUtc).ToList() : null));

    public async Task<RecurringResult> ExecuteAsync(Guid owner, Guid? id, int? year, int? month, Guid? accountId,
        Guid? categoryId, Func<RecurringSnapshot, RecurringResult> decide, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var r = Rules.SingleOrDefault(r => r.UserId == owner && r.Id == id);
            var a = await accounts.GetByIdAsync(owner, accountId ?? r?.AccountId ?? Guid.Empty, ct);
            var c = await categories.GetByIdAsync(owner, categoryId ?? r?.CategoryId ?? Guid.Empty, ct);
            var s = States.SingleOrDefault(s => s.UserId == owner && s.RecurringRuleId == id && s.Year == year && s.Month == month);
            var t = s?.TransactionId is { } tid ? await transactions.GetByIdAsync(owner, tid, ct) : null;
            var result = decide(new(r, a, c, s, t));
            if (result.Status != RecurringResultStatus.Ok) return result;
            switch (result.Change)
            {
                case RecurringChange.SaveRule: if (r is null) Rules.Add(result.Rule!); break;
                case RecurringChange.DeleteRule: Rules.Remove(r!); States.RemoveAll(s => s.RecurringRuleId == id); break;
                case RecurringChange.SaveState:
                    if (result.Transaction is not null) await transactions.TryAddAsync(result.Transaction, ct);
                    States.Add(result.State!); break;
                case RecurringChange.Restore: States.Remove(s!); break;
            }
            return result;
        }
        finally { gate.Release(); }
    }
}
