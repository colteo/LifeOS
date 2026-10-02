using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Recurring;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Recurring;

public sealed record RecurringSnapshot(RecurringTransactionRule? Rule, Account? Account, Category? Category,
    RecurringOccurrenceState? State, Transaction? Transaction);
public sealed record RecurringRead(IReadOnlyList<RecurringTransactionRule> Rules,
    IReadOnlyList<RecurringOccurrenceState> States, IReadOnlyList<Account> Accounts,
    IReadOnlyList<Transaction>? Transactions = null);
public enum RecurringChange { None, SaveRule, DeleteRule, SaveState, Restore }
public enum RecurringResultStatus { Ok, Invalid, NotFound, Conflict }
public sealed record RecurringResult(RecurringResultStatus Status, string? Field = null, string? Message = null,
    RecurringChange Change = RecurringChange.None, RecurringTransactionRule? Rule = null,
    RecurringOccurrenceState? State = null, Transaction? Transaction = null)
{
    public static RecurringResult Invalid(string field, string message) => new(RecurringResultStatus.Invalid, field, message);
    public static RecurringResult Missing() => new(RecurringResultStatus.NotFound);
    public static RecurringResult Conflict(string message) => new(RecurringResultStatus.Conflict, Message: message);
}

// Infrastructure supplies a coherent owned snapshot and commits the decision atomically.
public interface IRecurringRepository
{
    Task<RecurringRead> ReadAsync(Guid userId, int fromYear, int fromMonth, int toYear, int toMonth, CancellationToken cancellationToken,
        DateTimeOffset? transactionsFromUtc = null, DateTimeOffset? transactionsToUtc = null);
    Task<RecurringResult> ExecuteAsync(Guid userId, Guid? ruleId, int? year, int? month,
        Guid? accountId, Guid? categoryId, Func<RecurringSnapshot, RecurringResult> decide, CancellationToken cancellationToken);
}
