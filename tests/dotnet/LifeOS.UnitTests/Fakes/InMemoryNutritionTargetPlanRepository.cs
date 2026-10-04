using System.Reflection;
using LifeOS.Application.Nutrition;
using LifeOS.Domain.Nutrition;

namespace LifeOS.UnitTests.Fakes;

// Mirrors the EF Core repository: scoped by user, never stores overlapping plans of one user (the
// database trigger's guarantee), deleting a plan deletes its overrides, editing a plan drops its
// overrides outside the new period, one override per user and date. Plans are stored and returned as
// deep copies, so an edit is visible only after UpdateAsync.
internal sealed class InMemoryNutritionTargetPlanRepository : INutritionTargetPlanRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo RulesField =
        typeof(NutritionTargetPlan).GetField("_rules", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Lock _lock = new();

    public List<NutritionTargetPlan> Plans { get; } = [];

    public List<NutritionTargetOverride> Overrides { get; } = [];

    // Simulates a plan saved by a concurrent request after the handler's overlap check.
    public NutritionTargetPlan? SaveConcurrentlyBeforeNextWrite { get; set; }

    public Task<IReadOnlyList<NutritionTargetPlan>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<NutritionTargetPlan>>(Plans.Where(plan => plan.UserId == userId)
                .OrderBy(plan => plan.StartsOn).Select(Clone).ToList());
        }
    }

    public Task<NutritionTargetPlan?> GetAsync(Guid userId, Guid planId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var plan = Plans.SingleOrDefault(candidate => candidate.UserId == userId && candidate.Id == planId);

            return Task.FromResult(plan is null ? null : Clone(plan));
        }
    }

    public Task<NutritionTargetPlan?> FindOverlapAsync(Guid userId, DateOnly startsOn, DateOnly endsOn, Guid? excludingPlanId,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(FindOverlap(userId, startsOn, endsOn, excludingPlanId) is { } plan ? Clone(plan) : null);
        }
    }

    public Task<NutritionTargetPlanSave> AddAsync(NutritionTargetPlan plan, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ApplyConcurrentWrite();

            if (FindOverlap(plan.UserId, plan.StartsOn, plan.EndsOn, plan.Id) is { } conflict)
            {
                return Task.FromResult(new NutritionTargetPlanSave(NutritionTargetPlanSaveStatus.Overlap, Clone(conflict)));
            }

            Plans.Add(Clone(plan));

            return Task.FromResult(new NutritionTargetPlanSave(NutritionTargetPlanSaveStatus.Saved));
        }
    }

    public Task<NutritionTargetPlanSave> UpdateAsync(NutritionTargetPlan plan, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ApplyConcurrentWrite();
            var index = Plans.FindIndex(stored => stored.UserId == plan.UserId && stored.Id == plan.Id);

            if (index < 0)
            {
                return Task.FromResult(new NutritionTargetPlanSave(NutritionTargetPlanSaveStatus.NotFound));
            }

            if (FindOverlap(plan.UserId, plan.StartsOn, plan.EndsOn, plan.Id) is { } conflict)
            {
                return Task.FromResult(new NutritionTargetPlanSave(NutritionTargetPlanSaveStatus.Overlap, Clone(conflict)));
            }

            Plans[index] = Clone(plan);
            Overrides.RemoveAll(item => item.PlanId == plan.Id && !plan.Covers(item.Date));

            return Task.FromResult(new NutritionTargetPlanSave(NutritionTargetPlanSaveStatus.Saved));
        }
    }

    public Task<bool> DeleteAsync(Guid userId, Guid planId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var deleted = Plans.RemoveAll(plan => plan.UserId == userId && plan.Id == planId) == 1;
            Overrides.RemoveAll(item => item.PlanId == planId && deleted);

            return Task.FromResult(deleted);
        }
    }

    public Task<NutritionTargetDay> GetDayAsync(Guid userId, DateOnly date, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var plan = Plans.FirstOrDefault(candidate => candidate.UserId == userId && candidate.Covers(date));

            if (plan is null)
            {
                return Task.FromResult(NutritionTargetDay.None);
            }

            var item = Overrides.SingleOrDefault(candidate => candidate.UserId == userId && candidate.Date == date && candidate.PlanId == plan.Id);

            return Task.FromResult(new NutritionTargetDay(Clone(plan), item));
        }
    }

    public Task<bool> SetOverrideAsync(NutritionTargetOverride dailyOverride, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!Plans.Any(plan => plan.Id == dailyOverride.PlanId && plan.UserId == dailyOverride.UserId && plan.Covers(dailyOverride.Date)))
            {
                return Task.FromResult(false);
            }

            Overrides.RemoveAll(item => item.UserId == dailyOverride.UserId && item.Date == dailyOverride.Date);
            Overrides.Add(dailyOverride);

            return Task.FromResult(true);
        }
    }

    public Task RemoveOverrideAsync(Guid userId, DateOnly date, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Overrides.RemoveAll(item => item.UserId == userId && item.Date == date);
        }

        return Task.CompletedTask;
    }

    private NutritionTargetPlan? FindOverlap(Guid userId, DateOnly startsOn, DateOnly endsOn, Guid? excludingPlanId) =>
        Plans.Where(plan => plan.UserId == userId && plan.Id != excludingPlanId && plan.Overlaps(startsOn, endsOn))
            .OrderBy(plan => plan.StartsOn)
            .FirstOrDefault();

    private void ApplyConcurrentWrite()
    {
        if (SaveConcurrentlyBeforeNextWrite is { } plan)
        {
            Plans.Add(Clone(plan));
            SaveConcurrentlyBeforeNextWrite = null;
        }
    }

    private static NutritionTargetPlan Clone(NutritionTargetPlan plan)
    {
        var copy = (NutritionTargetPlan)CloneMethod.Invoke(plan, null)!;
        var rules = ((List<NutritionTargetDayRule>)RulesField.GetValue(plan)!)
            .Select(rule => (NutritionTargetDayRule)CloneMethod.Invoke(rule, null)!)
            .ToList();
        RulesField.SetValue(copy, rules);

        return copy;
    }
}
