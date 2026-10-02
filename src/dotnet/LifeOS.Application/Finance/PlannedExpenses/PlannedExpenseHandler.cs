using LifeOS.Application.Finance.Recurring;
using LifeOS.Domain.Finance.PlannedExpenses;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.PlannedExpenses;

public sealed record SavePlannedExpense(string Name, Guid AccountId, Guid CategoryId, decimal ExpectedAmount, DateOnly ScheduledDate, string? Note);
public sealed record ConfirmPlannedExpense(decimal Amount, string? Note, DateTimeOffset OccurredAtUtc);
public sealed record PlannedExpenseSummary(Guid Id, string Name, Guid AccountId, Guid CategoryId, string Currency,
    decimal ExpectedAmount, DateOnly ScheduledDate, string? Note, PlannedExpenseStatus Status, Guid? TransactionId,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record PlannedExpenseQueryResult(PlannedExpenseResultStatus Status, IReadOnlyList<PlannedExpenseSummary> Items, string? Message = null);

public sealed class PlannedExpenseHandler(IPlannedExpenseRepository repository, TimeProvider clock)
{
    public static IReadOnlyList<PlannedExpenseSummary> Project(PlannedExpenseRead read, DateOnly today)
    {
        var states = read.States.ToDictionary(s => s.PlannedExpenseId);
        var accounts = read.Accounts.ToDictionary(a => a.Id);
        return read.Items.OrderBy(i => i.ScheduledDate).ThenBy(i => i.Name).Select(i =>
        {
            states.TryGetValue(i.Id, out var state);
            return Summary(i, accounts[i.AccountId].Currency, today, state);
        }).ToList();
    }
    private static PlannedExpenseSummary Summary(PlannedExpense i, string currency, DateOnly today, PlannedExpenseState? state) =>
        new(i.Id, i.Name, i.AccountId, i.CategoryId, currency, i.ExpectedAmount, i.ScheduledDate, i.Note,
            i.Status(today, state), state?.TransactionId, i.CreatedAtUtc, i.UpdatedAtUtc);

    public async Task<PlannedExpenseQueryResult> QueryAsync(Guid userId, DateOnly from, DateOnly to, int offset, CancellationToken ct)
    {
        DateOnly today;
        try
        {
            today = RecurringHandler.LocalToday(clock, offset);
            if (to < from || to.DayNumber - from.DayNumber > 3660) throw new ArgumentException("Supply an inclusive date range of at most 3661 days.");
        }
        catch (ArgumentException e) { return new(PlannedExpenseResultStatus.Invalid, [], e.Message); }
        return new(PlannedExpenseResultStatus.Ok, Project(await repository.ReadAsync(userId, from, to, ct), today));
    }

    public async Task<PlannedExpenseQueryResult> GetAsync(Guid userId, Guid id, int offset, CancellationToken ct)
    {
        DateOnly today;
        try { today = RecurringHandler.LocalToday(clock, offset); }
        catch (ArgumentException e) { return new(PlannedExpenseResultStatus.Invalid, [], e.Message); }
        var s = await repository.GetAsync(userId, id, ct);
        return s.Item is null ? new(PlannedExpenseResultStatus.NotFound, [])
            : new(PlannedExpenseResultStatus.Ok, [Summary(s.Item, s.Account!.Currency, today, s.State)]);
    }

    public Task<PlannedExpenseResult> SaveAsync(Guid userId, Guid? id, SavePlannedExpense input, CancellationToken ct) =>
        repository.ExecuteAsync(userId, id, input.AccountId, input.CategoryId, s =>
        {
            if (id is not null && s.Item is null) return PlannedExpenseResult.Missing();
            if (s.State is not null) return PlannedExpenseResult.Conflict("Restore cancelled items before editing; confirmed items cannot be edited.");
            if (s.Account is null) return PlannedExpenseResult.Invalid("accountId", "Account does not exist.");
            if (s.Category is null) return PlannedExpenseResult.Invalid("categoryId", "Category does not exist.");
            try
            {
                var item = s.Item ?? PlannedExpense.Create(userId, input.Name, s.Account, s.Category, input.ExpectedAmount, input.ScheduledDate, input.Note, clock.GetUtcNow());
                if (s.Item is not null) item.Update(input.Name, s.Account, s.Category, input.ExpectedAmount, input.ScheduledDate, input.Note, clock.GetUtcNow(), s.State);
                return new(PlannedExpenseResultStatus.Ok, Change: PlannedExpenseChange.Save, Item: item);
            }
            catch (ArgumentException e) { return PlannedExpenseResult.Invalid(e.ParamName ?? "request", e.Message); }
        }, ct);

    public Task<PlannedExpenseResult> DeleteAsync(Guid userId, Guid id, CancellationToken ct) =>
        repository.ExecuteAsync(userId, id, null, null, s => s.Item is null ? PlannedExpenseResult.Missing()
            : new(PlannedExpenseResultStatus.Ok, Change: PlannedExpenseChange.Delete, Item: s.Item), ct);

    public Task<PlannedExpenseResult> ActAsync(Guid userId, Guid id, int offset, string action, ConfirmPlannedExpense? input, CancellationToken ct)
    {
        DateOnly today;
        try { today = RecurringHandler.LocalToday(clock, offset); }
        catch (ArgumentException e) { return Task.FromResult(PlannedExpenseResult.Invalid("utcOffsetMinutes", e.Message)); }
        return repository.ExecuteAsync(userId, id, null, null, s =>
        {
            if (s.Item is null) return PlannedExpenseResult.Missing();
            var status = s.Item.Status(today, s.State);
            if (action == "confirm" && status == PlannedExpenseStatus.Confirmed)
                return new(PlannedExpenseResultStatus.Ok, State: s.State, Transaction: s.Transaction);
            if (action == "restore") { s.Item.Touch(clock.GetUtcNow()); return status == PlannedExpenseStatus.Cancelled
                ? new(PlannedExpenseResultStatus.Ok, Change: PlannedExpenseChange.Restore, Item: s.Item, State: s.State)
                : PlannedExpenseResult.Conflict("Only a cancelled expense can be restored."); }
            if (action == "cancel" && status == PlannedExpenseStatus.Cancelled) return new(PlannedExpenseResultStatus.Ok, State: s.State);
            if (s.State is not null) return PlannedExpenseResult.Conflict("This planned expense has already been processed.");
            if (action == "cancel") return new(PlannedExpenseResultStatus.Ok, Change: PlannedExpenseChange.Process, Item: s.Item,
                State: PlannedExpenseState.Cancel(s.Item, today, clock.GetUtcNow()));
            if (action != "confirm" || input is null) return PlannedExpenseResult.Invalid("action", "Unknown action.");
            if (status != PlannedExpenseStatus.Due) return PlannedExpenseResult.Conflict("Future expenses cannot be confirmed early.");
            if (s.Account is null || s.Category is null) return PlannedExpenseResult.Conflict("A referenced resource is missing.");
            if (s.Category.CategoryType != LifeOS.Domain.Finance.Categories.CategoryType.Expense)
                return PlannedExpenseResult.Invalid("categoryId", "An Expense category is required.");
            try
            {
                var actual = Transaction.CreateExpense(userId, s.Account.Id, s.Category.Id, input.Amount, s.Account.Currency, input.OccurredAtUtc, input.Note, clock.GetUtcNow());
                return new(PlannedExpenseResultStatus.Ok, Change: PlannedExpenseChange.Process, Item: s.Item, Transaction: actual,
                    State: PlannedExpenseState.Confirm(s.Item, today, actual, clock.GetUtcNow()));
            }
            catch (ArgumentException e) { return PlannedExpenseResult.Invalid(e.ParamName ?? "request", e.Message); }
        }, ct);
    }
}
