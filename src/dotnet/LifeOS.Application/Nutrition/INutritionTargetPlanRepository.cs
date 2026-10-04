using LifeOS.Domain.Nutrition;

namespace LifeOS.Application.Nutrition;

// The plan covering a date (if any) and that date's override (if any).
public sealed record NutritionTargetDay(NutritionTargetPlan? Plan, NutritionTargetOverride? Override)
{
    public static readonly NutritionTargetDay None = new(null, null);
}

public enum NutritionTargetPlanSaveStatus
{
    Saved,
    NotFound,

    // Another plan of the user overlaps the period (it may have been saved concurrently).
    Overlap
}

public sealed record NutritionTargetPlanSave(NutritionTargetPlanSaveStatus Status, NutritionTargetPlan? Conflict = null);

// Nutrition target plans and daily overrides (NUT-003). Every method is scoped to userId: another
// user's plan is indistinguishable from a missing one.
//
// Overlap guarantee: AddAsync and UpdateAsync never persist a plan that overlaps another plan of the
// same user, also under concurrency (the database rejects it); they report Overlap instead.
public interface INutritionTargetPlanRepository
{
    // Ordered by StartsOn.
    Task<IReadOnlyList<NutritionTargetPlan>> ListAsync(Guid userId, CancellationToken cancellationToken);

    Task<NutritionTargetPlan?> GetAsync(Guid userId, Guid planId, CancellationToken cancellationToken);

    // The earliest plan of the user overlapping startsOn..endsOn (inclusive), other than excludingPlanId.
    Task<NutritionTargetPlan?> FindOverlapAsync(Guid userId, DateOnly startsOn, DateOnly endsOn, Guid? excludingPlanId,
        CancellationToken cancellationToken);

    Task<NutritionTargetPlanSave> AddAsync(NutritionTargetPlan plan, CancellationToken cancellationToken);

    // Saves the edited plan and, in the same transaction, deletes its daily overrides that fall outside
    // the new period. NotFound when the plan no longer exists.
    Task<NutritionTargetPlanSave> UpdateAsync(NutritionTargetPlan plan, CancellationToken cancellationToken);

    // Deletes the plan with its weekly rules and daily overrides. Meals and meal nutrition are untouched.
    Task<bool> DeleteAsync(Guid userId, Guid planId, CancellationToken cancellationToken);

    Task<NutritionTargetDay> GetDayAsync(Guid userId, DateOnly date, CancellationToken cancellationToken);

    // Inserts the override or replaces the date's existing one, only while its plan still covers the
    // date. False when it no longer does (nothing written).
    Task<bool> SetOverrideAsync(NutritionTargetOverride dailyOverride, CancellationToken cancellationToken);

    Task RemoveOverrideAsync(Guid userId, DateOnly date, CancellationToken cancellationToken);
}
