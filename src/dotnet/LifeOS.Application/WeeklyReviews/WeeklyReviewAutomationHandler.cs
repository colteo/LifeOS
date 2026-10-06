using System.Globalization;
using LifeOS.Application.Automation;
using LifeOS.Application.Users.SetTimeZone;
using LifeOS.Domain.Notifications;
using LifeOS.Domain.WeeklyReviews;

namespace LifeOS.Application.WeeklyReviews;

// AUTO-002: the weekly review automation. Sunday 20:00 in the user's IANA zone, 24 h lateness, one
// occurrence per local week keyed by its Sunday ("2026-10-04"). The engine (AUTO-001) claims each
// occurrence once, retries it, and commits the review, the fenced completion and the WeeklyReviewReady
// deliveries in one transaction. Read-only towards Finance, Gym and Nutrition.
public sealed class WeeklyReviewAutomationHandler(
    IWeeklyReviewRepository reviews,
    WeeklyReviewSnapshotBuilder snapshots,
    TimeProvider clock) : IAutomationHandler
{
    public const string Type = "WeeklyReview";
    public const string ResourceType = "weekly_review";
    public const DayOfWeek Day = DayOfWeek.Sunday;
    public const string InvalidOccurrenceKeyCode = "InvalidOccurrenceKey";
    public const string InvalidTimeZoneCode = "InvalidTimeZone";

    public static readonly TimeOnly Time = new(20, 0);
    public static readonly TimeSpan Lateness = TimeSpan.FromHours(24);

    public string AutomationType => Type;

    public TimeSpan MaxLateness => Lateness;

    public async Task<IReadOnlyList<DueOccurrence>> FindDueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return [];
        }

        var open = new Dictionary<string, (WeeklyReviewOpenZone Zone, DateTimeOffset DueAtUtc)>(StringComparer.Ordinal);

        foreach (var zoneId in (await reviews.GetUserTimeZoneIdsAsync(cancellationToken)).Order(StringComparer.Ordinal))
        {
            // An invalid stored zone schedules nothing (AUTO-001 §6: no zone ⇒ no automation).
            if (!TryFindZone(zoneId, out var zone)
                || LocalSchedule.ResolveWeekly(zone, nowUtc, Day, Time, Lateness) is not { } occurrence)
            {
                continue;
            }

            open[zoneId] = (new WeeklyReviewOpenZone(zoneId, OccurrenceKey(occurrence.LocalDate), occurrence.LocalDate), occurrence.DueAtUtc);
        }

        if (open.Count == 0)
        {
            return [];
        }

        var due = await reviews.FindDueUsersAsync(Type, open.Values.Select(entry => entry.Zone).ToList(), limit, cancellationToken);

        return due
            .Where(user => open.ContainsKey(user.TimeZoneId))
            .Select(user =>
            {
                var (zone, dueAtUtc) = open[user.TimeZoneId];
                return new DueOccurrence(user.UserId, zone.OccurrenceKey, zone.TimeZoneId, dueAtUtc);
            })
            .ToList();
    }

    public async Task<AutomationResult> ExecuteAsync(AutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        if (!TryParseOccurrenceKey(occurrence.OccurrenceKey, out var weekEndDate))
        {
            return AutomationResult.PermanentFailure(InvalidOccurrenceKeyCode);
        }

        // The zone the occurrence was resolved with, never the user's current one (AUTO-002 D-9).
        if (!TryFindZone(occurrence.TimeZoneId, out var zone))
        {
            return AutomationResult.PermanentFailure(InvalidTimeZoneCode);
        }

        if (!await reviews.IsEnabledAsync(occurrence.UserId, cancellationToken))
        {
            return AutomationResult.NotApplicable();
        }

        // Defensive: a review for this week already exists. Record success, never notify again.
        if (await reviews.FindIdAsync(occurrence.UserId, weekEndDate, cancellationToken) is { } existingId)
        {
            return AutomationResult.Succeeded(existingId);
        }

        var period = WeeklyReviewPeriod.For(weekEndDate, zone);
        var snapshot = await snapshots.BuildAsync(occurrence.UserId, period, zone, cancellationToken);
        var review = WeeklyReview.Create(occurrence.UserId, weekEndDate, occurrence.TimeZoneId, clock.GetUtcNow(), snapshot);

        return AutomationResult.Succeeded(
            review.Id,
            new AutomationNotification(NotificationType.WeeklyReviewReady, ResourceType, review.Id),
            saveArtifact: transaction => reviews.TryAddAsync(review, transaction));
    }

    public static string OccurrenceKey(DateOnly weekEndDate) => weekEndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool TryParseOccurrenceKey(string? key, out DateOnly weekEndDate) =>
        DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out weekEndDate)
        && WeeklyReview.IsWeekEnd(weekEndDate);

    private static bool TryFindZone(string? zoneId, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;

        if (!SetTimeZoneHandler.TryNormalizeIanaTimeZone(zoneId, out var normalized))
        {
            return false;
        }

        zone = TimeZoneInfo.FindSystemTimeZoneById(normalized);
        return true;
    }
}
