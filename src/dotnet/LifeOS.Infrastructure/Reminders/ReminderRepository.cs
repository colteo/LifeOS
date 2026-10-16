using LifeOS.Application.Reminders;
using LifeOS.Domain.Reminders;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Reminders;

// Every query filters by user_id; a foreign id behaves exactly like a missing one. The habit names of
// HabitIncomplete labels are read with plain SQL from habits (no Habits type is referenced).
internal sealed class ReminderRepository(LifeOSDbContext dbContext) : IReminderRepository
{
    public Task<int> CountOpenAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Reminders.CountAsync(reminder => reminder.UserId == userId && reminder.Status != ReminderStatus.Completed, cancellationToken);

    public async Task AddAsync(Reminder reminder, CancellationToken cancellationToken)
    {
        dbContext.Reminders.Add(reminder);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception, ReminderConfiguration.OnePerHabitIndexName))
        {
            dbContext.ChangeTracker.Clear();
            throw new ReminderConflictException(ReminderConflictException.ExistsForHabit, "This habit already has a reminder.", exception);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception, ReminderConfiguration.OneHydrationIndexName))
        {
            dbContext.ChangeTracker.Clear();
            throw new ReminderConflictException(ReminderConflictException.HydrationExists, "You already have a water reminder.", exception);
        }
    }

    public Task<Reminder?> GetAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken) =>
        dbContext.Reminders.SingleOrDefaultAsync(reminder => reminder.UserId == userId && reminder.Id == reminderId, cancellationToken);

    public async Task<IReadOnlyList<Reminder>> GetAllAsync(Guid userId, DateTimeOffset completedSince, CancellationToken cancellationToken) =>
        await dbContext.Reminders
            .AsNoTracking()
            .Where(reminder => reminder.UserId == userId
                && (reminder.Status != ReminderStatus.Completed || reminder.UpdatedAtUtc >= completedSince || reminder.SnoozeUntilUtc != null))
            .OrderBy(reminder => reminder.CreatedAtUtc)
            .ThenBy(reminder => reminder.Id)
            .ToListAsync(cancellationToken);

    public async Task<bool> SaveAsync(Reminder reminder, CancellationToken cancellationToken)
    {
        if (dbContext.Entry(reminder).State == EntityState.Detached)
        {
            throw new ArgumentException("Save a reminder obtained from GetAsync in the same scope.", nameof(reminder));
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted by another request in the meantime.
            dbContext.ChangeTracker.Clear();
            return false;
        }

        return true;
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid reminderId, CancellationToken cancellationToken) =>
        await dbContext.Reminders
            .Where(reminder => reminder.UserId == userId && reminder.Id == reminderId)
            .ExecuteDeleteAsync(cancellationToken) == 1;

    public async Task<IReadOnlyDictionary<Guid, string>> GetHabitNamesAsync(Guid userId, IReadOnlyCollection<Guid> habitIds,
        CancellationToken cancellationToken)
    {
        if (habitIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = habitIds.ToArray();
        var rows = await dbContext.Database
            .SqlQuery<HabitNameRow>($"""SELECT h.id AS "Id", h.name AS "Name" FROM habits AS h WHERE h.user_id = {userId} AND h.id = ANY({ids})""")
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.Id, row => row.Name);
    }

    private sealed class HabitNameRow
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";
    }
}
