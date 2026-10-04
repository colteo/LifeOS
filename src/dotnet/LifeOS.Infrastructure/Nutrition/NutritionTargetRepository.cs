using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Nutrition;

// Concurrency (NUT-003): a save is one INSERT ... ON CONFLICT (user_id, effective_from) DO UPDATE, so
// concurrent Set/Remove requests for the same user and day leave exactly one state (the last committed
// one) and never a duplicate history row. Rows are never deleted.
internal sealed class NutritionTargetRepository(LifeOSDbContext dbContext) : INutritionTargetRepository
{
    public Task<NutritionTarget?> GetEffectiveAsync(Guid userId, DateOnly date, CancellationToken cancellationToken) =>
        dbContext.NutritionTargets
            .AsNoTracking()
            .Where(target => target.UserId == userId && target.EffectiveFrom <= date)
            .OrderByDescending(target => target.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task SaveAsync(NutritionTarget target, CancellationToken cancellationToken)
    {
        var source = target.Source.ToString();

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO nutrition_targets
                (id, user_id, effective_from, calories_kcal, protein_grams, carbs_grams, fat_grams, source, created_at_utc, updated_at_utc)
            VALUES ({target.Id}, {target.UserId}, {target.EffectiveFrom}, {target.CaloriesKcal}::numeric, {target.ProteinGrams}::numeric,
                    {target.CarbsGrams}::numeric, {target.FatGrams}::numeric, {source}, {target.CreatedAtUtc}, {target.UpdatedAtUtc})
            ON CONFLICT (user_id, effective_from) DO UPDATE SET
                calories_kcal = EXCLUDED.calories_kcal,
                protein_grams = EXCLUDED.protein_grams,
                carbs_grams = EXCLUDED.carbs_grams,
                fat_grams = EXCLUDED.fat_grams,
                source = EXCLUDED.source,
                updated_at_utc = EXCLUDED.updated_at_utc
            """, cancellationToken);
    }
}
