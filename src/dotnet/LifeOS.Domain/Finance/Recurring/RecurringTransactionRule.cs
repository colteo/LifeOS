using LifeOS.Domain.Finance.Budgets;
using LifeOS.Domain.Finance.Transactions;

namespace LifeOS.Domain.Finance.Recurring;

public sealed class RecurringTransactionRule
{
    private RecurringTransactionRule() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Name { get; private set; } = "";
    public TransactionType TransactionType { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid CategoryId { get; private set; }
    public decimal Amount { get; private set; }
    public int DayOfMonth { get; private set; }
    public int StartYear { get; private set; }
    public int StartMonth { get; private set; }
    public int? EndYear { get; private set; }
    public int? EndMonth { get; private set; }
    public string? Note { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static RecurringTransactionRule Create(Guid userId, string name, TransactionType type,
        Guid accountId, Guid categoryId, decimal amount, int dayOfMonth, int startYear, int startMonth,
        string? note, DateTimeOffset now, int? endYear = null, int? endMonth = null)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A valid owner is required.", nameof(userId));
        if (type is not (TransactionType.Income or TransactionType.Expense))
            throw new ArgumentException("Only Income and Expense are supported.", nameof(type));
        MonthlyBudget.ValidateMonth(startYear, startMonth);
        var rule = new RecurringTransactionRule { Id = Guid.CreateVersion7(), UserId = userId,
            TransactionType = type, StartYear = startYear, StartMonth = startMonth, CreatedAtUtc = now.ToUniversalTime() };
        rule.Update(name, accountId, categoryId, amount, dayOfMonth, note, now, endYear, endMonth);
        return rule;
    }

    public void Update(string name, Guid accountId, Guid categoryId, decimal amount, int dayOfMonth,
        string? note, DateTimeOffset now, int? endYear = null, int? endMonth = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (accountId == Guid.Empty) throw new ArgumentException("Account is required.", nameof(accountId));
        if (categoryId == Guid.Empty) throw new ArgumentException("Category is required.", nameof(categoryId));
        if (amount <= 0 || amount > Transaction.MaxAmount) throw new ArgumentOutOfRangeException(nameof(amount));
        if (decimal.Round(amount, Transaction.MaxDecimalPlaces) != amount)
            throw new ArgumentException("Amount must have at most 4 decimal places.", nameof(amount));
        if (dayOfMonth is < 1 or > 31) throw new ArgumentOutOfRangeException(nameof(dayOfMonth));
        if (endYear.HasValue != endMonth.HasValue)
            throw new ArgumentException("End year and month must both be present, or both absent for no end.", nameof(endMonth));
        if (endYear is { } finalYear && endMonth is { } finalMonth)
        {
            MonthlyBudget.ValidateMonth(finalYear, finalMonth);
            if (finalYear * 12 + finalMonth < StartYear * 12 + StartMonth)
                throw new ArgumentException("End month cannot be before the start month.", nameof(endMonth));
        }
        Name = name.Trim(); AccountId = accountId; CategoryId = categoryId; Amount = amount;
        DayOfMonth = dayOfMonth; Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        EndYear = endYear; EndMonth = endMonth;
        UpdatedAtUtc = now.ToUniversalTime();
    }

    public bool IncludesMonth(int year, int month)
    {
        MonthlyBudget.ValidateMonth(year, month);
        var logicalMonth = year * 12 + month;
        return logicalMonth >= StartYear * 12 + StartMonth
            && (EndYear is null || logicalMonth <= EndYear.Value * 12 + EndMonth!.Value);
    }

    public DateOnly ScheduledDate(int year, int month)
    {
        if (!IncludesMonth(year, month))
            throw new ArgumentException("The occurrence is outside the rule's start/end month range.", nameof(month));
        return new(year, month, Math.Min(DayOfMonth, DateTime.DaysInMonth(year, month)));
    }

    public OccurrenceStatus Status(int year, int month, DateOnly today, RecurringOccurrenceState? state = null)
    {
        var date = ScheduledDate(year, month);
        if (state is not null && (state.RecurringRuleId != Id || state.UserId != UserId || state.Year != year || state.Month != month))
            throw new ArgumentException("State must match the owned logical month.", nameof(state));
        return state?.Status ?? (date > today ? OccurrenceStatus.Projected : OccurrenceStatus.Due);
    }
}
