namespace LifeOS.Domain.Finance.Recurring;

public enum OccurrenceStatus { Projected, Due, Confirmed, Skipped }

public sealed class RecurringOccurrenceState
{
    private RecurringOccurrenceState() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid RecurringRuleId { get; private set; }
    public int Year { get; private set; }
    public int Month { get; private set; }
    public DateOnly ScheduledDate { get; private set; }
    public OccurrenceStatus Status { get; private set; }
    public Guid? TransactionId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public static RecurringOccurrenceState Create(RecurringTransactionRule rule, int year, int month,
        OccurrenceStatus status, Guid? transactionId, DateTimeOffset now)
    {
        if (status is not (OccurrenceStatus.Confirmed or OccurrenceStatus.Skipped)
            || (status == OccurrenceStatus.Confirmed) != (transactionId is not null)
            || transactionId == Guid.Empty)
            throw new ArgumentException("Confirmed requires a transaction; Skipped forbids one.", nameof(status));
        return new() { Id = Guid.CreateVersion7(), UserId = rule.UserId, RecurringRuleId = rule.Id,
            Year = year, Month = month, ScheduledDate = rule.ScheduledDate(year, month), Status = status,
            TransactionId = transactionId, CreatedAtUtc = now.ToUniversalTime() };
    }
}
