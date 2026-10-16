using LifeOS.Application.Reminders;
using LifeOS.Domain.Reminders;

namespace LifeOS.UnitTests.Fakes;

// The discovery prefilter and maintenance writes of the PostgreSQL store over InMemoryReminderRepository's
// rows. TimeZones stands in for users.time_zone_id, DisabledUsers for user_reminders_enabled = false
// (Custom reminders), DisabledKinds for habit_/hydration_reminders_enabled = false, Executions for
// automation_executions (key → terminal). The SQL itself is proven in ReminderTickTests.
internal sealed class InMemoryReminderScheduleStore(InMemoryReminderRepository repository) : IReminderScheduleStore
{
    public Dictionary<Guid, string?> TimeZones { get; } = [];

    public HashSet<Guid> DisabledUsers { get; } = [];

    public HashSet<(Guid UserId, ReminderKind Kind)> DisabledKinds { get; } = [];

    public List<IReadOnlyList<ReminderKind>> CandidateKinds { get; } = [];

    public Dictionary<string, bool> Executions { get; } = new(StringComparer.Ordinal);

    public List<(IReadOnlyList<MissedOccurrence> Occurrences, IReadOnlyList<MissedSnooze> Snoozes)> Skipped { get; } = [];

    public List<(Guid UserId, ReminderOccurrence Occurrence, ReminderOutcome Outcome)> Advanced { get; } = [];

    public Task<IReadOnlyList<string>> GetUserTimeZoneIdsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(TimeZones.Values.OfType<string>().Distinct().ToList());

    public Task<IReadOnlyList<ReminderCandidate>> FindCandidatesAsync(IReadOnlyList<ReminderKind> kinds, IReadOnlyList<ZoneLocalNow> zones,
        DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
    {
        var localNows = zones.ToDictionary(zone => zone.TimeZoneId, zone => zone.LocalNow);
        CandidateKinds.Add(kinds);

        return Task.FromResult<IReadOnlyList<ReminderCandidate>>(repository.Reminders
            .Where(reminder => TimeZones.GetValueOrDefault(reminder.UserId) is { } zone && localNows.ContainsKey(zone)
                && kinds.Contains(reminder.Kind)
                && !(reminder.Kind == ReminderKind.Custom && DisabledUsers.Contains(reminder.UserId))
                && !DisabledKinds.Contains((reminder.UserId, reminder.Kind))
                && reminder.Status != ReminderStatus.Paused
                && ((reminder.Status == ReminderStatus.Active && reminder.NextDueLocal <= localNows[zone]) || reminder.SnoozeUntilUtc <= nowUtc))
            .OrderBy(reminder => reminder.NextDueLocal ?? DateTime.MaxValue)
            .ThenBy(reminder => reminder.Id)
            .Take(limit)
            .Select(reminder => new ReminderCandidate(reminder.Id, reminder.UserId, TimeZones[reminder.UserId]!, reminder.Status, reminder.Schedule,
                reminder.NextDueLocal, reminder.SnoozeUntilUtc))
            .ToList());
    }

    public Task<IReadOnlyDictionary<string, bool>> GetExecutionsAsync(string automationType, IReadOnlyList<(Guid UserId, string OccurrenceKey)> occurrences,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, bool>>(occurrences
            .Where(occurrence => Executions.ContainsKey(occurrence.OccurrenceKey))
            .ToDictionary(occurrence => occurrence.OccurrenceKey, occurrence => Executions[occurrence.OccurrenceKey], StringComparer.Ordinal));

    public Task SkipMissedAsync(IReadOnlyList<MissedOccurrence> occurrences, IReadOnlyList<MissedSnooze> snoozes, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        Skipped.Add((occurrences, snoozes));
        return Task.CompletedTask;
    }

    public Task<Reminder?> FindAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken) =>
        repository.GetAsync(userId, reminderId, cancellationToken);

    public Task AdvanceAsync(Guid userId, ReminderOccurrence occurrence, ReminderOutcome outcome, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        Advanced.Add((userId, occurrence, outcome));

        if (repository.Find(userId, occurrence.ReminderId) is { } reminder)
        {
            if (occurrence.SnoozeUtc is { } snooze)
            {
                reminder.AdvanceSnooze(snooze, outcome, nowUtc);
            }
            else
            {
                reminder.Advance(occurrence.Local!.Value, outcome, nowUtc);
            }
        }

        return Task.CompletedTask;
    }
}
