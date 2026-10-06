using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.UnitTests.Fakes;

// Same rules as the PostgreSQL repository under one lock: users with a zone, the optional enabled
// setting (no row = enabled), at most one review per user and week, user-scoped reads. Discovery
// skips occurrences that already have a row in the (optional) shared execution store. The SQL itself
// is proven against PostgreSQL in the integration tests.
internal sealed class InMemoryWeeklyReviewRepository(InMemoryAutomationExecutionStore? executions = null) : IWeeklyReviewRepository
{
    private readonly Lock _lock = new();

    // User id → stored users.time_zone_id (null = none).
    public Dictionary<Guid, string?> Users { get; } = [];

    public Dictionary<Guid, WeeklyReviewSettings> Settings { get; } = [];

    public List<WeeklyReview> Reviews { get; } = [];

    // Users that exist for SetSettingsAsync; null = every user exists.
    public HashSet<Guid>? ExistingUsers { get; set; }

    public int AddAttempts { get; private set; }

    // Makes IsEnabledAsync throw, to simulate a failing read during an attempt.
    public bool FailReads { get; set; }

    public void AddUser(Guid userId, string? timeZoneId = "Europe/Rome")
    {
        lock (_lock)
        {
            Users[userId] = timeZoneId;
        }
    }

    public Task<IReadOnlyList<string>> GetUserTimeZoneIdsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<string>>(Users.Values.OfType<string>().Distinct(StringComparer.Ordinal).ToList());
        }
    }

    public Task<IReadOnlyList<WeeklyReviewDueUser>> FindDueUsersAsync(
        string automationType,
        IReadOnlyList<WeeklyReviewOpenZone> openZones,
        int limit,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var due = Users
                .Where(user => user.Value is not null)
                .SelectMany(user => openZones
                    .Where(zone => zone.TimeZoneId == user.Value)
                    .Select(zone => (UserId: user.Key, Zone: zone)))
                .Where(candidate => !Settings.TryGetValue(candidate.UserId, out var settings) || settings.Enabled)
                .Where(candidate => executions is null || !executions.Rows.Any(row =>
                    row.UserId == candidate.UserId && row.AutomationType == automationType && row.OccurrenceKey == candidate.Zone.OccurrenceKey))
                .Where(candidate => !Reviews.Any(review => review.UserId == candidate.UserId && review.WeekEndDate == candidate.Zone.WeekEndDate))
                .OrderBy(candidate => candidate.UserId)
                .Take(limit)
                .Select(candidate => new WeeklyReviewDueUser(candidate.UserId, candidate.Zone.TimeZoneId))
                .ToList();

            return Task.FromResult<IReadOnlyList<WeeklyReviewDueUser>>(due);
        }
    }

    public Task<bool> IsEnabledAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (FailReads)
            {
                throw new InvalidOperationException("Simulated read failure.");
            }

            return Task.FromResult(Settings.TryGetValue(userId, out var settings) ? settings.Enabled : WeeklyReviewSettings.DefaultEnabled);
        }
    }

    public Task<bool> SetSettingsAsync(WeeklyReviewSettings settings, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (ExistingUsers is not null && !ExistingUsers.Contains(settings.UserId))
            {
                return Task.FromResult(false);
            }

            Settings[settings.UserId] = settings;
            return Task.FromResult(true);
        }
    }

    public Task<Guid?> FindIdAsync(Guid userId, DateOnly weekEndDate, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Reviews.SingleOrDefault(review => review.UserId == userId && review.WeekEndDate == weekEndDate)?.Id);
        }
    }

    public Task<bool> TryAddAsync(WeeklyReview review, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            AddAttempts++;

            if (Reviews.Any(existing => existing.UserId == review.UserId && existing.WeekEndDate == review.WeekEndDate))
            {
                return Task.FromResult(false);
            }

            Reviews.Add(review);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<WeeklyReviewListItem>> GetPageAsync(Guid userId, DateOnly? beforeWeekEndDate, int take, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<WeeklyReviewListItem>>(Reviews
                .Where(review => review.UserId == userId && (beforeWeekEndDate is null || review.WeekEndDate < beforeWeekEndDate))
                .OrderByDescending(review => review.WeekEndDate)
                .Take(take)
                .Select(review => new WeeklyReviewListItem(review.Id, review.WeekStartDate, review.WeekEndDate, review.GeneratedAtUtc))
                .ToList());
        }
    }

    public Task<WeeklyReview?> GetAsync(Guid userId, Guid reviewId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Reviews.SingleOrDefault(review => review.Id == reviewId && review.UserId == userId));
        }
    }
}
