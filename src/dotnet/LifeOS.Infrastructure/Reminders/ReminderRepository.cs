using LifeOS.Application.Reminders;
using LifeOS.Domain.Reminders;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Reminders;

// Every query filters by user_id; a foreign id behaves exactly like a missing one.
internal sealed class ReminderRepository(LifeOSDbContext dbContext) : IReminderRepository
{
    public Task<int> CountOpenAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Reminders.CountAsync(reminder => reminder.UserId == userId && reminder.Status != ReminderStatus.Completed, cancellationToken);

    public async Task AddAsync(Reminder reminder, CancellationToken cancellationToken)
    {
        dbContext.Reminders.Add(reminder);
        await dbContext.SaveChangesAsync(cancellationToken);
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
}
