using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Nutrition;

// Every query filters by user_id; a foreign id behaves exactly like a missing one.
internal sealed class MealEntryRepository(LifeOSDbContext dbContext) : IMealEntryRepository
{
    public async Task<IReadOnlyList<MealEntry>> GetForDiaryDateAsync(Guid userId, DateOnly diaryDate, CancellationToken cancellationToken) =>
        await dbContext.MealEntries
            .AsNoTracking()
            .Where(entry => entry.UserId == userId && entry.DiaryDate == diaryDate)
            .OrderByDescending(entry => entry.DiaryTime)
            .ThenByDescending(entry => entry.Id)
            .ToListAsync(cancellationToken);

    public Task<MealEntry?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        dbContext.MealEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.UserId == userId && entry.Id == id, cancellationToken);

    public async Task AddAsync(MealEntry entry, CancellationToken cancellationToken)
    {
        dbContext.MealEntries.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.Entry(entry).State = EntityState.Detached;
    }

    // The diary date, owner and creation time never change, so only the edited columns are written.
    // With clearNutrition, the update (which takes the meal's row lock) and the snapshot deletion are
    // one transaction: a concurrent estimate of the old description cannot survive it.
    public async Task<bool> UpdateAsync(MealEntry entry, bool clearNutrition, CancellationToken cancellationToken)
    {
        if (!clearNutrition)
        {
            return await UpdateColumnsAsync(entry, cancellationToken);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (!await UpdateColumnsAsync(entry, cancellationToken))
        {
            return false;
        }

        await dbContext.MealNutritionSnapshots
            .Where(snapshot => snapshot.MealEntryId == entry.Id)
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    // The nutrition snapshot is removed by its ON DELETE CASCADE foreign key.
    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        await dbContext.MealEntries
            .Where(entry => entry.UserId == userId && entry.Id == id)
            .ExecuteDeleteAsync(cancellationToken) == 1;

    private async Task<bool> UpdateColumnsAsync(MealEntry entry, CancellationToken cancellationToken) =>
        await dbContext.MealEntries
            .Where(stored => stored.UserId == entry.UserId && stored.Id == entry.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(stored => stored.Description, entry.Description)
                .SetProperty(stored => stored.MealType, entry.MealType)
                .SetProperty(stored => stored.DiaryTime, entry.DiaryTime)
                .SetProperty(stored => stored.OccurredAtUtc, entry.OccurredAtUtc)
                .SetProperty(stored => stored.UpdatedAtUtc, entry.UpdatedAtUtc), cancellationToken) == 1;
}
