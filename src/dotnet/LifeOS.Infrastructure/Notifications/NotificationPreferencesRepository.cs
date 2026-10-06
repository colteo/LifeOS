using LifeOS.Application.Notifications;
using LifeOS.Domain.Notifications;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Notifications;

// AUTO-003A on PostgreSQL. The write is one INSERT … ON CONFLICT (user_id) DO UPDATE; a missing user is
// the user FK violation. Reads are scoped to one user; no row = the defaults.
internal sealed class NotificationPreferencesRepository(LifeOSDbContext db) : INotificationPreferencesRepository
{
    public async Task<NotificationPreferences> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.NotificationPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(preferences => preferences.UserId == userId, cancellationToken)
        ?? NotificationPreferences.Default(userId);

    public async Task<bool> SetAsync(NotificationPreferences preferences, CancellationToken cancellationToken)
    {
        try
        {
            return await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notification_preferences
                    (user_id, recurring_transaction_reminders_enabled, planned_expense_reminders_enabled,
                     quiet_hours_start, quiet_hours_end, updated_at_utc)
                VALUES
                    ({preferences.UserId}, {preferences.RecurringTransactionRemindersEnabled}, {preferences.PlannedExpenseRemindersEnabled},
                     {preferences.QuietHoursStart}, {preferences.QuietHoursEnd}, {preferences.UpdatedAtUtc})
                ON CONFLICT (user_id) DO UPDATE SET
                    recurring_transaction_reminders_enabled = EXCLUDED.recurring_transaction_reminders_enabled,
                    planned_expense_reminders_enabled = EXCLUDED.planned_expense_reminders_enabled,
                    quiet_hours_start = EXCLUDED.quiet_hours_start,
                    quiet_hours_end = EXCLUDED.quiet_hours_end,
                    updated_at_utc = EXCLUDED.updated_at_utc
                """, cancellationToken) == 1;
        }
        catch (Exception exception) when (PostgresErrors.IsForeignKeyViolation(exception, NotificationPreferencesConfiguration.UserForeignKeyName))
        {
            return false;
        }
    }

    public async Task<QuietHoursContext?> GetQuietHoursContextAsync(Guid userId, CancellationToken cancellationToken)
    {
        var row = await (
                from user in db.Users.AsNoTracking()
                where user.Id == userId
                join saved in db.NotificationPreferences.AsNoTracking() on user.Id equals saved.UserId into preferences
                from saved in preferences.DefaultIfEmpty()
                select new
                {
                    user.TimeZoneId,
                    Start = saved == null ? (TimeOnly?)null : saved.QuietHoursStart,
                    End = saved == null ? (TimeOnly?)null : saved.QuietHoursEnd
                })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var quietHours = row.Start is { } start && row.End is { } end ? QuietHours.Create(start, end) : QuietHours.Default;

        return new QuietHoursContext(row.TimeZoneId, quietHours);
    }
}
