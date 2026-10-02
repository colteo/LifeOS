namespace LifeOS.Contracts.Finance.PlannedExpenses;

public sealed record SavePlannedExpenseRequest(string Name, Guid AccountId, Guid CategoryId, decimal ExpectedAmount, DateOnly ScheduledDate, string? Note);
public sealed record ConfirmPlannedExpenseRequest(decimal Amount, string? Note, DateTimeOffset OccurredAtUtc);
public sealed record PlannedExpenseResponse(Guid Id, string Name, Guid AccountId, Guid CategoryId, string Currency,
    decimal ExpectedAmount, DateOnly ScheduledDate, string? Note, string Status, Guid? TransactionId,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record PlannedExpenseActionResponse(Guid? TransactionId);

public sealed record PlannedExpenseSavedResponse(Guid Id);
