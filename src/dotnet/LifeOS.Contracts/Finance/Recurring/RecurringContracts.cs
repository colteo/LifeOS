namespace LifeOS.Contracts.Finance.Recurring;

public sealed record SaveRecurringRuleRequest(string Name, string Type, Guid AccountId, Guid CategoryId,
    decimal Amount, int DayOfMonth, int StartYear, int StartMonth, string? Note);
public sealed record RecurringRuleResponse(Guid Id, string Name, string Type, Guid AccountId, Guid CategoryId,
    decimal Amount, int DayOfMonth, int StartYear, int StartMonth, string? Note);
public sealed record RecurringOccurrenceResponse(Guid RuleId, string Name, string Type, Guid AccountId, Guid CategoryId,
    string Currency, decimal ExpectedAmount, string? Note, int Year, int Month, DateOnly ScheduledDate,
    string Status, Guid? TransactionId);
public sealed record RecurringResponse(IReadOnlyList<RecurringRuleResponse> Rules, IReadOnlyList<RecurringOccurrenceResponse> Occurrences);
public sealed record ConfirmRecurringRequest(decimal Amount, string? Note, DateTimeOffset OccurredAtUtc);
public sealed record RecurringActionResponse(Guid? TransactionId);
