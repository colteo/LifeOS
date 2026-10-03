using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.PlannedExpenses;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Application.Finance.PlannedExpenses;

public sealed record PlannedExpenseSnapshot(PlannedExpense? Item, Account? Account, Category? Category,
    PlannedExpenseState? State, Transaction? Transaction);
public sealed record PlannedExpenseRead(IReadOnlyList<PlannedExpense> Items, IReadOnlyList<PlannedExpenseState> States,
    IReadOnlyList<Account> Accounts);
public enum PlannedExpenseChange { None, Save, Delete, Process, Restore }
public enum PlannedExpenseResultStatus { Ok, Invalid, NotFound, Conflict }
public sealed record PlannedExpenseResult(PlannedExpenseResultStatus Status, string? Field = null, string? Message = null,
    PlannedExpenseChange Change = PlannedExpenseChange.None, PlannedExpense? Item = null,
    PlannedExpenseState? State = null, Transaction? Transaction = null)
{
    public static PlannedExpenseResult Invalid(string field, string message) => new(PlannedExpenseResultStatus.Invalid, field, message);
    public static PlannedExpenseResult Missing() => new(PlannedExpenseResultStatus.NotFound);
    public static PlannedExpenseResult Conflict(string message) => new(PlannedExpenseResultStatus.Conflict, Message: message);
}

public interface IPlannedExpenseRepository
{
    Task<PlannedExpenseRead> ReadAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct);
    Task<PlannedExpenseSnapshot> GetAsync(Guid userId, Guid id, CancellationToken ct);
    Task<PlannedExpenseResult> ExecuteAsync(Guid userId, Guid? id, Guid? accountId, Guid? categoryId,
        Func<PlannedExpenseSnapshot, PlannedExpenseResult> decide, CancellationToken ct);
}
