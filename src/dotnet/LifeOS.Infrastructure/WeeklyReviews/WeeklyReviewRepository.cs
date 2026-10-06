using LifeOS.Application.WeeklyReviews;
using LifeOS.Domain.WeeklyReviews;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.WeeklyReviews;

// AUTO-002 on PostgreSQL. Writes are single statements on the scope's DbContext, so they join the
// automation completion's unit of work:
// - a review = INSERT … ON CONFLICT (user_id, week_end_date) DO NOTHING (the unique index decides);
// - the setting = INSERT … ON CONFLICT (user_id) DO UPDATE.
// Discovery is one ids-only query over the users of the open zones (AUTO-001 §7).
internal sealed class WeeklyReviewRepository(LifeOSDbContext db) : IWeeklyReviewRepository
{
    public async Task<IReadOnlyList<string>> GetUserTimeZoneIdsAsync(CancellationToken cancellationToken) =>
        await db.Users
            .AsNoTracking()
            .Where(user => user.TimeZoneId != null)
            .Select(user => user.TimeZoneId!)
            .Distinct()
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WeeklyReviewDueUser>> FindDueUsersAsync(
        string automationType,
        IReadOnlyList<WeeklyReviewOpenZone> openZones,
        int limit,
        CancellationToken cancellationToken)
    {
        if (openZones.Count == 0 || limit <= 0)
        {
            return [];
        }

        var zones = openZones.Select(zone => zone.TimeZoneId).ToArray();
        var keys = openZones.Select(zone => zone.OccurrenceKey).ToArray();
        var weekEnds = openZones.Select(zone => zone.WeekEndDate).ToArray();

        var rows = await db.Database
            .SqlQuery<DueUserRow>($"""
                SELECT u.id AS "UserId", u.time_zone_id AS "TimeZoneId"
                FROM users AS u
                JOIN unnest({zones}::text[], {keys}::text[], {weekEnds}::date[]) AS open_zone(time_zone_id, occurrence_key, week_end_date)
                  ON u.time_zone_id = open_zone.time_zone_id
                WHERE NOT EXISTS (
                        SELECT 1 FROM weekly_review_settings AS s
                        WHERE s.user_id = u.id AND NOT s.enabled)
                  AND NOT EXISTS (
                        SELECT 1 FROM automation_executions AS e
                        WHERE e.user_id = u.id AND e.automation_type = {automationType}
                          AND e.occurrence_key = open_zone.occurrence_key)
                  AND NOT EXISTS (
                        SELECT 1 FROM weekly_reviews AS r
                        WHERE r.user_id = u.id AND r.week_end_date = open_zone.week_end_date)
                ORDER BY u.id
                LIMIT {limit}
                """)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new WeeklyReviewDueUser(row.UserId, row.TimeZoneId)).ToList();
    }

    public async Task<bool> IsEnabledAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.Set<WeeklyReviewSettings>()
            .AsNoTracking()
            .Where(settings => settings.UserId == userId)
            .Select(settings => (bool?)settings.Enabled)
            .SingleOrDefaultAsync(cancellationToken)
        ?? WeeklyReviewSettings.DefaultEnabled;

    public async Task<bool> SetSettingsAsync(WeeklyReviewSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            return await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO weekly_review_settings (user_id, enabled, updated_at_utc)
                VALUES ({settings.UserId}, {settings.Enabled}, {settings.UpdatedAtUtc})
                ON CONFLICT (user_id) DO UPDATE SET
                    enabled = EXCLUDED.enabled,
                    updated_at_utc = EXCLUDED.updated_at_utc
                """, cancellationToken) == 1;
        }
        catch (Exception exception) when (PostgresErrors.IsForeignKeyViolation(exception, WeeklyReviewSettingsConfiguration.UserForeignKeyName))
        {
            return false;
        }
    }

    public async Task<Guid?> FindIdAsync(Guid userId, DateOnly weekEndDate, CancellationToken cancellationToken) =>
        await db.Set<WeeklyReviewRecord>()
            .AsNoTracking()
            .Where(review => review.UserId == userId && review.WeekEndDate == weekEndDate)
            .Select(review => (Guid?)review.Id)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> TryAddAsync(WeeklyReview review, CancellationToken cancellationToken)
    {
        var row = WeeklyReviewRecord.From(review);

        return await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO weekly_reviews
                (id, user_id, week_start_date, week_end_date, time_zone_id, generated_at_utc, data_version, snapshot)
            VALUES
                ({row.Id}, {row.UserId}, {row.WeekStartDate}, {row.WeekEndDate}, {row.TimeZoneId},
                 {row.GeneratedAtUtc}, {row.DataVersion}, CAST({row.Snapshot} AS jsonb))
            ON CONFLICT (user_id, week_end_date) DO NOTHING
            """, cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<WeeklyReviewListItem>> GetPageAsync(
        Guid userId,
        DateOnly? beforeWeekEndDate,
        int take,
        CancellationToken cancellationToken)
    {
        var reviews = db.Set<WeeklyReviewRecord>().AsNoTracking().Where(review => review.UserId == userId);

        if (beforeWeekEndDate is { } before)
        {
            reviews = reviews.Where(review => review.WeekEndDate < before);
        }

        return await reviews
            .OrderByDescending(review => review.WeekEndDate)
            .Take(take)
            .Select(review => new WeeklyReviewListItem(review.Id, review.WeekStartDate, review.WeekEndDate, review.GeneratedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<WeeklyReview?> GetAsync(Guid userId, Guid reviewId, CancellationToken cancellationToken)
    {
        var row = await db.Set<WeeklyReviewRecord>()
            .AsNoTracking()
            .SingleOrDefaultAsync(review => review.Id == reviewId && review.UserId == userId, cancellationToken);

        return row?.ToDomain();
    }

    private sealed class DueUserRow
    {
        public Guid UserId { get; set; }

        public string TimeZoneId { get; set; } = "";
    }
}
