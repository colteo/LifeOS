using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;
using LifeOS.Infrastructure.Persistence;
using LifeOS.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Nutrition;

// Overlap guarantee (NUT-003): the trigger trg_nutrition_target_plans_no_overlap runs on every insert
// and on every update of a plan's period. It takes a transaction-scoped advisory lock for the user and
// then checks for an overlapping plan, raising exclusion_violation (23P01) under
// ex_nutrition_target_plans_no_overlap. Plan writes of one user are therefore serialized and the
// second of two concurrent overlapping writes always sees the first. Here that error becomes Overlap.
//
// Overrides: one INSERT ... SELECT that share-locks the covering plan row, with the unique
// (user_id, date) index as the conflict target. A plan edit (row lock) deletes its overrides outside
// the new period in the same transaction; a plan delete cascades to them. So an override never
// outlives its plan or lands outside it, whichever request commits first.
internal sealed class NutritionTargetPlanRepository(LifeOSDbContext dbContext) : INutritionTargetPlanRepository
{
    public async Task<IReadOnlyList<NutritionTargetPlan>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.NutritionTargetPlans
            .AsNoTracking()
            .Where(plan => plan.UserId == userId)
            .OrderBy(plan => plan.StartsOn)
            .ToListAsync(cancellationToken);

    // Tracked: the caller may edit it and pass it to UpdateAsync within the same request.
    public Task<NutritionTargetPlan?> GetAsync(Guid userId, Guid planId, CancellationToken cancellationToken) =>
        dbContext.NutritionTargetPlans
            .SingleOrDefaultAsync(plan => plan.UserId == userId && plan.Id == planId, cancellationToken);

    public Task<NutritionTargetPlan?> FindOverlapAsync(Guid userId, DateOnly startsOn, DateOnly endsOn, Guid? excludingPlanId,
        CancellationToken cancellationToken) =>
        dbContext.NutritionTargetPlans
            .AsNoTracking()
            .Where(plan => plan.UserId == userId
                && plan.Id != excludingPlanId
                && plan.StartsOn <= endsOn
                && startsOn <= plan.EndsOn)
            .OrderBy(plan => plan.StartsOn)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<NutritionTargetPlanSave> AddAsync(NutritionTargetPlan plan, CancellationToken cancellationToken)
    {
        dbContext.NutritionTargetPlans.Add(plan);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsOverlap(exception))
        {
            dbContext.ChangeTracker.Clear();
            return await OverlapAsync(plan, cancellationToken);
        }

        return new(NutritionTargetPlanSaveStatus.Saved);
    }

    public async Task<NutritionTargetPlanSave> UpdateAsync(NutritionTargetPlan plan, CancellationToken cancellationToken)
    {
        if (dbContext.Entry(plan).State == EntityState.Detached)
        {
            throw new ArgumentException("Update a plan obtained from GetAsync in the same scope.", nameof(plan));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.ChangeTracker.Clear();
            return new(NutritionTargetPlanSaveStatus.NotFound);
        }
        catch (DbUpdateException exception) when (IsOverlap(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            return await OverlapAsync(plan, cancellationToken);
        }

        await dbContext.NutritionTargetOverrides
            .Where(item => item.PlanId == plan.Id && (item.Date < plan.StartsOn || item.Date > plan.EndsOn))
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return new(NutritionTargetPlanSaveStatus.Saved);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid planId, CancellationToken cancellationToken) =>
        await dbContext.NutritionTargetPlans
            .Where(plan => plan.UserId == userId && plan.Id == planId)
            .ExecuteDeleteAsync(cancellationToken) == 1;

    public async Task<NutritionTargetDay> GetDayAsync(Guid userId, DateOnly date, CancellationToken cancellationToken)
    {
        var plan = await dbContext.NutritionTargetPlans
            .AsNoTracking()
            .Where(candidate => candidate.UserId == userId && candidate.StartsOn <= date && date <= candidate.EndsOn)
            .FirstOrDefaultAsync(cancellationToken);

        if (plan is null)
        {
            return NutritionTargetDay.None;
        }

        var dailyOverride = await dbContext.NutritionTargetOverrides
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.Date == date && item.PlanId == plan.Id, cancellationToken);

        return new(plan, dailyOverride);
    }

    public async Task<bool> SetOverrideAsync(NutritionTargetOverride dailyOverride, CancellationToken cancellationToken)
    {
        var mode = dailyOverride.Mode.ToString();

        return await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO nutrition_target_overrides
                (id, user_id, plan_id, date, mode, calories_kcal, protein_grams, carbs_grams, fat_grams, created_at_utc, updated_at_utc)
            SELECT {dailyOverride.Id}, plan.user_id, plan.id, {dailyOverride.Date}, {mode},
                   {dailyOverride.CaloriesKcal}::numeric, {dailyOverride.ProteinGrams}::numeric, {dailyOverride.CarbsGrams}::numeric,
                   {dailyOverride.FatGrams}::numeric, {dailyOverride.CreatedAtUtc}, {dailyOverride.UpdatedAtUtc}
            FROM nutrition_target_plans AS plan
            WHERE plan.id = {dailyOverride.PlanId} AND plan.user_id = {dailyOverride.UserId}
              AND plan.starts_on <= {dailyOverride.Date} AND plan.ends_on >= {dailyOverride.Date}
            FOR SHARE OF plan
            ON CONFLICT (user_id, date) DO UPDATE SET
                plan_id = EXCLUDED.plan_id,
                mode = EXCLUDED.mode,
                calories_kcal = EXCLUDED.calories_kcal,
                protein_grams = EXCLUDED.protein_grams,
                carbs_grams = EXCLUDED.carbs_grams,
                fat_grams = EXCLUDED.fat_grams,
                updated_at_utc = EXCLUDED.updated_at_utc
            """, cancellationToken) == 1;
    }

    public Task RemoveOverrideAsync(Guid userId, DateOnly date, CancellationToken cancellationToken) =>
        dbContext.NutritionTargetOverrides
            .Where(item => item.UserId == userId && item.Date == date)
            .ExecuteDeleteAsync(cancellationToken);

    private static bool IsOverlap(Exception exception) =>
        PostgresErrors.IsExclusionViolation(exception, NutritionTargetPlanConfiguration.NoOverlapConstraintName);

    private async Task<NutritionTargetPlanSave> OverlapAsync(NutritionTargetPlan plan, CancellationToken cancellationToken) =>
        new(NutritionTargetPlanSaveStatus.Overlap,
            await FindOverlapAsync(plan.UserId, plan.StartsOn, plan.EndsOn, plan.Id, cancellationToken));
}
