using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Nutrition;

// User scoping goes through the owning meal (snapshots have no user column of their own).
//
// Concurrency (NUT-002): every snapshot write is one INSERT ... SELECT that share-locks the owning meal
// row, with the unique meal_entry_id index as the conflict target.
//   - Bulk/lazy inserts use ON CONFLICT DO NOTHING: concurrent runs leave exactly one snapshot and
//     never overwrite an existing (confirmed or user-adjusted) one.
//   - Explicit saves use ON CONFLICT DO UPDATE: confirm/edit replaces whatever is current, so an
//     explicit result always wins over a racing lazy close, in either order.
//   - A meal update that changes the description takes the meal's row lock before deleting the
//     snapshot (MealEntryRepository), so an estimate of the old text either lands before and is
//     deleted, or re-checks the description after the update commits and inserts nothing.
internal sealed class MealNutritionRepository(LifeOSDbContext dbContext) : IMealNutritionRepository
{
    public async Task<IReadOnlyList<MealWithNutrition>> GetDayAsync(Guid userId, DateOnly diaryDate, CancellationToken cancellationToken) =>
        await WithNutrition(dbContext.MealEntries.Where(meal => meal.UserId == userId && meal.DiaryDate == diaryDate))
            .ToListAsync(cancellationToken);

    public Task<MealWithNutrition?> GetMealAsync(Guid userId, Guid mealEntryId, CancellationToken cancellationToken) =>
        WithNutrition(dbContext.MealEntries.Where(meal => meal.UserId == userId && meal.Id == mealEntryId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<MealEntry>> GetUnanalyzedBeforeAsync(Guid userId, DateOnly beforeDate, int limit,
        CancellationToken cancellationToken) =>
        await dbContext.MealEntries
            .AsNoTracking()
            .Where(meal => meal.UserId == userId
                && meal.DiaryDate < beforeDate
                && !dbContext.MealNutritionSnapshots.Any(snapshot => snapshot.MealEntryId == meal.Id))
            .OrderByDescending(meal => meal.DiaryDate)
            .ThenBy(meal => meal.DiaryTime)
            .ThenBy(meal => meal.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

    public async Task<bool> SaveAsync(Guid userId, MealNutritionSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!MealNutritionSnapshot.MayReplaceExisting(snapshot.Source))
        {
            throw new ArgumentException($"{snapshot.Source} nutrition cannot replace a snapshot; use AddIfMissingAsync.", nameof(snapshot));
        }

        var source = snapshot.Source.ToString();

        return await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO meal_nutrition_snapshots
                (id, meal_entry_id, calories_kcal, protein_grams, carbs_grams, fat_grams, source, created_at_utc, updated_at_utc)
            SELECT {snapshot.Id}, meal.id, {snapshot.CaloriesKcal}, {snapshot.ProteinGrams}, {snapshot.CarbsGrams},
                   {snapshot.FatGrams}, {source}, {snapshot.CreatedAtUtc}, {snapshot.UpdatedAtUtc}
            FROM meal_entries AS meal
            WHERE meal.id = {snapshot.MealEntryId} AND meal.user_id = {userId}
            FOR SHARE OF meal
            ON CONFLICT (meal_entry_id) DO UPDATE SET
                calories_kcal = EXCLUDED.calories_kcal,
                protein_grams = EXCLUDED.protein_grams,
                carbs_grams = EXCLUDED.carbs_grams,
                fat_grams = EXCLUDED.fat_grams,
                source = EXCLUDED.source,
                updated_at_utc = EXCLUDED.updated_at_utc
            """, cancellationToken) == 1;
    }

    public async Task<bool> AddIfMissingAsync(Guid userId, MealNutritionSnapshot snapshot, string estimatedDescription,
        CancellationToken cancellationToken)
    {
        var source = snapshot.Source.ToString();

        return await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO meal_nutrition_snapshots
                (id, meal_entry_id, calories_kcal, protein_grams, carbs_grams, fat_grams, source, created_at_utc, updated_at_utc)
            SELECT {snapshot.Id}, meal.id, {snapshot.CaloriesKcal}, {snapshot.ProteinGrams}, {snapshot.CarbsGrams},
                   {snapshot.FatGrams}, {source}, {snapshot.CreatedAtUtc}, {snapshot.UpdatedAtUtc}
            FROM meal_entries AS meal
            WHERE meal.id = {snapshot.MealEntryId} AND meal.user_id = {userId} AND meal.description = {estimatedDescription}
            FOR SHARE OF meal
            ON CONFLICT (meal_entry_id) DO NOTHING
            """, cancellationToken) == 1;
    }

    // Each meal with its snapshot (left join), in journal order: newest first.
    private IQueryable<MealWithNutrition> WithNutrition(IQueryable<MealEntry> meals) =>
        from meal in meals.AsNoTracking()
        join stored in dbContext.MealNutritionSnapshots.AsNoTracking() on meal.Id equals stored.MealEntryId into snapshots
        from snapshot in snapshots.DefaultIfEmpty()
        orderby meal.DiaryTime descending, meal.Id descending
        select new MealWithNutrition(meal, snapshot);
}
