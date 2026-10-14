using LifeOS.Application.Habits;
using LifeOS.Domain.Habits;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Habits;

// Every query filters by user_id; a foreign id behaves exactly like a missing one.
//
// Check-ins: one INSERT ... SELECT from the owner's habit row (share-locked), with the (habit_id, date)
// primary key as the conflict target, so concurrent saves of one date leave one row (last write wins)
// and a check-in can never be written for another user's or a deleted habit.
internal sealed class HabitRepository(LifeOSDbContext dbContext) : IHabitRepository
{
    public Task<int> CountActiveAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Habits.CountAsync(habit => habit.UserId == userId && habit.Status == HabitStatus.Active, cancellationToken);

    public async Task AddAsync(Habit habit, CancellationToken cancellationToken)
    {
        dbContext.Habits.Add(habit);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task<Habit?> GetAsync(Guid userId, Guid habitId, CancellationToken cancellationToken) =>
        dbContext.Habits
            .Include(habit => habit.Schedule)
            .SingleOrDefaultAsync(habit => habit.UserId == userId && habit.Id == habitId, cancellationToken);

    public async Task<IReadOnlyList<Habit>> GetAllAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.Habits
            .AsNoTracking()
            .Include(habit => habit.Schedule)
            .Where(habit => habit.UserId == userId)
            .OrderBy(habit => habit.CreatedAtUtc)
            .ThenBy(habit => habit.Id)
            .ToListAsync(cancellationToken);

    public async Task<bool> SaveAsync(Habit habit, CancellationToken cancellationToken)
    {
        if (dbContext.Entry(habit).State == EntityState.Detached)
        {
            throw new ArgumentException("Save a habit obtained from GetAsync in the same scope.", nameof(habit));
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
        catch (DbUpdateException exception) when (PostgresErrors.IsForeignKeyViolation(exception, HabitConfiguration.VersionHabitForeignKeyName))
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }

        return true;
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid habitId, CancellationToken cancellationToken) =>
        await dbContext.Habits
            .Where(habit => habit.UserId == userId && habit.Id == habitId)
            .ExecuteDeleteAsync(cancellationToken) == 1;

    public async Task<IReadOnlyList<HabitCheckIn>> GetCheckInsAsync(Guid userId, IReadOnlyCollection<Guid> habitIds, DateOnly from, DateOnly to,
        CancellationToken cancellationToken) =>
        await dbContext.HabitCheckIns
            .AsNoTracking()
            .Where(checkIn => checkIn.UserId == userId && habitIds.Contains(checkIn.HabitId) && checkIn.Date >= from && checkIn.Date <= to)
            .OrderBy(checkIn => checkIn.Date)
            .ToListAsync(cancellationToken);

    public async Task<bool> UpsertCheckInAsync(HabitCheckIn checkIn, CancellationToken cancellationToken)
    {
        var status = checkIn.Status.ToString();

        return await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO habit_check_ins (habit_id, user_id, date, status, note, created_at_utc, updated_at_utc)
            SELECT habit.id, habit.user_id, {checkIn.Date}, {status}, {checkIn.Note}::varchar, {checkIn.CreatedAtUtc}, {checkIn.UpdatedAtUtc}
            FROM habits AS habit
            WHERE habit.id = {checkIn.HabitId} AND habit.user_id = {checkIn.UserId}
            FOR SHARE OF habit
            ON CONFLICT (habit_id, date) DO UPDATE SET
                status = EXCLUDED.status,
                note = EXCLUDED.note,
                updated_at_utc = EXCLUDED.updated_at_utc
            """, cancellationToken) == 1;
    }

    public async Task<bool> RemoveCheckInAsync(Guid userId, Guid habitId, DateOnly date, CancellationToken cancellationToken) =>
        await dbContext.HabitCheckIns
            .Where(checkIn => checkIn.UserId == userId && checkIn.HabitId == habitId && checkIn.Date == date)
            .ExecuteDeleteAsync(cancellationToken) == 1;
}
