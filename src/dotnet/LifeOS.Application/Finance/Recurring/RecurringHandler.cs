using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Recurring;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.Recurring;

public sealed record SaveRecurringRule(string Name, TransactionType Type, Guid AccountId, Guid CategoryId,
    decimal Amount, int DayOfMonth, int StartYear, int StartMonth, string? Note, int? EndYear = null, int? EndMonth = null);
public sealed record ConfirmRecurring(decimal Amount, string? Note, DateTimeOffset OccurredAtUtc);
public sealed record RecurringOccurrence(Guid RuleId, string Name, TransactionType Type, Guid AccountId, Guid CategoryId,
    string Currency, decimal ExpectedAmount, string? Note, int Year, int Month, DateOnly ScheduledDate,
    OccurrenceStatus Status, Guid? TransactionId);
public sealed record RecurringQueryResult(RecurringResultStatus Status, IReadOnlyList<RecurringTransactionRule> Rules,
    IReadOnlyList<RecurringOccurrence> Occurrences, string? Message = null, IReadOnlyList<Transaction>? Transactions = null);

public sealed class RecurringHandler(IRecurringRepository repository, TimeProvider clock)
{
    public const int MaxQueryMonths = 120;
    public static DateOnly LocalToday(TimeProvider clock, int offset)
    {
        if (offset is < -840 or > 840) throw new ArgumentOutOfRangeException(nameof(offset), "UTC offset must be between -840 and 840 minutes.");
        return DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromMinutes(offset)).DateTime);
    }

    public async Task<RecurringQueryResult> QueryAsync(Guid userId, int fromYear, int fromMonth,
        int toYear, int toMonth, int offset, CancellationToken ct,
        DateTimeOffset? transactionsFromUtc = null, DateTimeOffset? transactionsToUtc = null)
    {
        DateOnly today;
        try
        {
            MonthlyBudget.ValidateMonth(fromYear, fromMonth); MonthlyBudget.ValidateMonth(toYear, toMonth);
            var length = (toYear - fromYear) * 12 + toMonth - fromMonth + 1;
            if (length is < 1 or > MaxQueryMonths) throw new ArgumentException($"Query must contain 1–{MaxQueryMonths} calendar months.");
            today = LocalToday(clock, offset);
        }
        catch (ArgumentException e) { return new(RecurringResultStatus.Invalid, [], [], e.Message); }
        var read = await repository.ReadAsync(userId, fromYear, fromMonth, toYear, toMonth, ct, transactionsFromUtc, transactionsToUtc);
        var states = read.States.ToDictionary(s => (s.RecurringRuleId, s.Year, s.Month));
        var accounts = read.Accounts.ToDictionary(a => a.Id);
        var occurrences = new List<RecurringOccurrence>();
        foreach (var rule in read.Rules)
        {
            for (var index = fromYear * 12 + fromMonth - 1; index <= toYear * 12 + toMonth - 1; index++)
            {
                var year = index / 12; var month = index % 12 + 1;
                if (!rule.IncludesMonth(year, month)) continue;
                states.TryGetValue((rule.Id, year, month), out var state);
                occurrences.Add(new(rule.Id, rule.Name, rule.TransactionType, rule.AccountId, rule.CategoryId,
                    accounts[rule.AccountId].Currency, rule.Amount, rule.Note, year, month,
                    state?.ScheduledDate ?? rule.ScheduledDate(year, month), rule.Status(year, month, today, state), state?.TransactionId));
            }
        }
        return new(RecurringResultStatus.Ok, read.Rules, occurrences.OrderBy(o => o.ScheduledDate).ThenBy(o => o.Name).ToList(), Transactions: read.Transactions);
    }

    public Task<RecurringResult> SaveAsync(Guid userId, Guid? id, SaveRecurringRule input, CancellationToken ct) =>
        repository.ExecuteAsync(userId, id, null, null, input.AccountId, input.CategoryId, snapshot =>
        {
            if (id is not null && snapshot.Rule is null) return RecurringResult.Missing();
            var type = snapshot.Rule?.TransactionType ?? input.Type;
            if (snapshot.Rule is not null && (input.Type != type || input.StartYear != snapshot.Rule.StartYear || input.StartMonth != snapshot.Rule.StartMonth))
                return RecurringResult.Invalid("type", "Type and start month cannot be changed.");
            if (snapshot.Account is null) return RecurringResult.Invalid("accountId", "Account does not exist.");
            if (snapshot.Category is null) return RecurringResult.Invalid("categoryId", "Category does not exist.");
            if (LifeOS.Application.Finance.Transactions.TransactionInputResolver.CategoryCompatibilityError(type, snapshot.Category.CategoryType) is { } compatibilityError)
                return RecurringResult.Invalid("categoryId", compatibilityError);
            try
            {
                var rule = snapshot.Rule ?? RecurringTransactionRule.Create(userId, input.Name, type, input.AccountId,
                    input.CategoryId, input.Amount, input.DayOfMonth, input.StartYear, input.StartMonth, input.Note, clock.GetUtcNow(), input.EndYear, input.EndMonth);
                if (snapshot.Rule is not null) rule.Update(input.Name, input.AccountId, input.CategoryId,
                    input.Amount, input.DayOfMonth, input.Note, clock.GetUtcNow(), input.EndYear, input.EndMonth);
                return new(RecurringResultStatus.Ok, Change: RecurringChange.SaveRule, Rule: rule);
            }
            catch (ArgumentException e) { return RecurringResult.Invalid(e.ParamName ?? "request", e.Message); }
        }, ct);

    public Task<RecurringResult> DeleteAsync(Guid userId, Guid id, CancellationToken ct) =>
        repository.ExecuteAsync(userId, id, null, null, null, null, s => s.Rule is null ? RecurringResult.Missing()
            : new(RecurringResultStatus.Ok, Change: RecurringChange.DeleteRule, Rule: s.Rule), ct);

    public Task<RecurringResult> ActAsync(Guid userId, Guid id, int year, int month, int offset,
        string action, ConfirmRecurring? input, CancellationToken ct)
    {
        DateOnly today;
        try { MonthlyBudget.ValidateMonth(year, month); today = LocalToday(clock, offset); }
        catch (ArgumentException e) { return Task.FromResult(RecurringResult.Invalid(e.ParamName ?? "request", e.Message)); }
        return repository.ExecuteAsync(userId, id, year, month, null, null, s =>
        {
            if (s.Rule is null) return RecurringResult.Missing();
            // A later end-range edit cannot invalidate a successful confirmation retry.
            // Its persisted link is audit data even when the month is no longer planned.
            if (action == "confirm" && s.State?.Status == OccurrenceStatus.Confirmed)
                return new(RecurringResultStatus.Ok, State: s.State, Transaction: s.Transaction);
            OccurrenceStatus status;
            try { status = s.Rule.Status(year, month, today, s.State); }
            catch (ArgumentException e) { return RecurringResult.Invalid("month", e.Message); }
            if (action == "restore")
                return status == OccurrenceStatus.Skipped
                    ? new(RecurringResultStatus.Ok, Change: RecurringChange.Restore, State: s.State)
                    : RecurringResult.Conflict("Only a skipped occurrence can be restored.");
            if (action == "skip" && status == OccurrenceStatus.Skipped) return new(RecurringResultStatus.Ok, State: s.State);
            if (status is OccurrenceStatus.Confirmed or OccurrenceStatus.Skipped)
                return RecurringResult.Conflict("This logical month has already been processed.");
            if (action == "skip") return new(RecurringResultStatus.Ok, Change: RecurringChange.SaveState,
                State: RecurringOccurrenceState.Create(s.Rule, year, month, OccurrenceStatus.Skipped, null, clock.GetUtcNow()));
            if (action != "confirm" || input is null) return RecurringResult.Invalid("action", "Unknown action.");
            if (status != OccurrenceStatus.Due) return RecurringResult.Conflict("Future occurrences cannot be confirmed early.");
            if (s.Account is null || s.Category is null) return RecurringResult.Conflict("A referenced resource is missing.");
            if (LifeOS.Application.Finance.Transactions.TransactionInputResolver.CategoryCompatibilityError(s.Rule.TransactionType, s.Category.CategoryType) is { } compatibilityError)
                return RecurringResult.Invalid("categoryId", compatibilityError);
            try
            {
                var transaction = s.Rule.TransactionType == TransactionType.Income
                    ? Transaction.CreateIncome(userId, s.Account.Id, s.Category.Id, input.Amount, s.Account.Currency, input.OccurredAtUtc, input.Note, clock.GetUtcNow())
                    : Transaction.CreateExpense(userId, s.Account.Id, s.Category.Id, input.Amount, s.Account.Currency, input.OccurredAtUtc, input.Note, clock.GetUtcNow());
                return new(RecurringResultStatus.Ok, Change: RecurringChange.SaveState, Transaction: transaction,
                    State: RecurringOccurrenceState.Create(s.Rule, year, month, OccurrenceStatus.Confirmed, transaction.Id, clock.GetUtcNow()));
            }
            catch (ArgumentException e) { return RecurringResult.Invalid(e.ParamName ?? "request", e.Message); }
        }, ct);
    }
}
