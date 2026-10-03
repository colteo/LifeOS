using LifeOS.Domain.Finance.Accounts;
using LifeOS.Domain.Finance.Categories;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Domain.Finance.PlannedExpenses;

public enum PlannedExpenseStatus { Projected, Due, Confirmed, Cancelled }

public sealed class PlannedExpense
{
    private PlannedExpense() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = "";
    public Guid AccountId { get; private set; }
    public Guid CategoryId { get; private set; }
    public decimal ExpectedAmount { get; private set; }
    public DateOnly ScheduledDate { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static PlannedExpense Create(Guid userId, string name, Account account, Category category,
        decimal amount, DateOnly scheduledDate, string? note, DateTimeOffset now)
    {
        if (userId == Guid.Empty) throw new ArgumentException("An owner is required.", nameof(userId));
        var item = new PlannedExpense { Id = Guid.CreateVersion7(), UserId = userId, CreatedAtUtc = now.ToUniversalTime() };
        item.Update(name, account, category, amount, scheduledDate, note, now);
        return item;
    }

    public void Update(string name, Account account, Category category, decimal amount, DateOnly scheduledDate,
        string? note, DateTimeOffset now, PlannedExpenseState? state = null)
    {
        ValidateState(state);
        if (state is not null) throw new InvalidOperationException("Restore cancelled items before editing; confirmed items cannot be edited.");
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (account.UserId != UserId) throw new ArgumentException("Account must belong to the owner.", nameof(account));
        if (category.UserId != UserId || category.CategoryType != CategoryType.Expense)
            throw new ArgumentException("An owned Expense category is required.", nameof(category));
        if (amount <= 0 || amount > Transaction.MaxAmount) throw new ArgumentOutOfRangeException(nameof(amount));
        if (decimal.Round(amount, Transaction.MaxDecimalPlaces) != amount)
            throw new ArgumentException("Amount must have at most 4 decimal places.", nameof(amount));
        // Npgsql represents the DateOnly endpoints as PostgreSQL infinities, not calendar dates.
        if (scheduledDate == DateOnly.MinValue || scheduledDate == DateOnly.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(scheduledDate), "Choose a finite calendar date between 0001-01-02 and 9999-12-30.");
        Name = name.Trim(); AccountId = account.Id; CategoryId = category.Id; ExpectedAmount = amount;
        ScheduledDate = scheduledDate; Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        UpdatedAtUtc = now.ToUniversalTime();
    }

    public void Touch(DateTimeOffset now) => UpdatedAtUtc = now.ToUniversalTime();

    public PlannedExpenseStatus Status(DateOnly today, PlannedExpenseState? state = null)
    {
        ValidateState(state);
        return state?.Status ?? (ScheduledDate > today ? PlannedExpenseStatus.Projected : PlannedExpenseStatus.Due);
    }

    private void ValidateState(PlannedExpenseState? state)
    {
        if (state is not null && (state.PlannedExpenseId != Id || state.UserId != UserId))
            throw new ArgumentException("State must belong to this planned expense.", nameof(state));
    }
}

public sealed class PlannedExpenseState
{
    private PlannedExpenseState() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid PlannedExpenseId { get; private set; }
    public PlannedExpenseStatus Status { get; private set; }
    public Guid? TransactionId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static PlannedExpenseState Cancel(PlannedExpense item, DateOnly today, DateTimeOffset now)
    {
        if (item.Status(today) is not (PlannedExpenseStatus.Due or PlannedExpenseStatus.Projected))
            throw new InvalidOperationException("Only unresolved expenses can be cancelled.");
        return new() { Id = Guid.CreateVersion7(), UserId = item.UserId, PlannedExpenseId = item.Id,
            Status = PlannedExpenseStatus.Cancelled, CreatedAtUtc = now.ToUniversalTime() };
    }

    public static PlannedExpenseState Confirm(PlannedExpense item, DateOnly today, Transaction transaction, DateTimeOffset now)
    {
        if (item.Status(today) != PlannedExpenseStatus.Due) throw new InvalidOperationException("Future expenses cannot be confirmed early.");
        if (transaction.UserId != item.UserId || transaction.TransactionType != TransactionType.Expense
            || transaction.AccountId != item.AccountId || transaction.CategoryId != item.CategoryId)
            throw new ArgumentException("Confirmation requires the matching owned Expense Transaction.", nameof(transaction));
        return new() { Id = Guid.CreateVersion7(), UserId = item.UserId, PlannedExpenseId = item.Id,
            Status = PlannedExpenseStatus.Confirmed, TransactionId = transaction.Id, CreatedAtUtc = now.ToUniversalTime() };
    }
}
