using System.Collections.Concurrent;
using LifeOS.Application.Automation;

namespace LifeOS.UnitTests.Fakes;

// Test-only IAutomationHandler: proves the automation engine without any business module.
// FindDueAsync returns Due (it does not exclude claimed occurrences: the store's claim does).
internal sealed class TestAutomationHandler(string automationType = "TestAutomation", TimeSpan? maxLateness = null) : IAutomationHandler
{
    public string AutomationType => automationType;

    public TimeSpan MaxLateness => maxLateness ?? TimeSpan.FromHours(24);

    public List<DueOccurrence> Due { get; } = [];

    public List<int> Limits { get; } = [];

    public ConcurrentQueue<AutomationOccurrence> Executed { get; } = new();

    // Default: success with no artifact.
    public Func<AutomationOccurrence, Task<AutomationResult>> Execute { get; set; } =
        _ => Task.FromResult(AutomationResult.Succeeded());

    public Task<IReadOnlyList<DueOccurrence>> FindDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
    {
        lock (Limits)
        {
            Limits.Add(limit);
        }

        return Task.FromResult<IReadOnlyList<DueOccurrence>>(Due.Take(limit).ToList());
    }

    public Task<AutomationResult> ExecuteAsync(AutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        Executed.Enqueue(occurrence);

        return Execute(occurrence);
    }

    public DueOccurrence AddDue(Guid userId, string occurrenceKey, DateTimeOffset scheduledForUtc, string timeZoneId = "Europe/Rome")
    {
        var due = new DueOccurrence(userId, occurrenceKey, timeZoneId, scheduledForUtc);
        Due.Add(due);

        return due;
    }
}
