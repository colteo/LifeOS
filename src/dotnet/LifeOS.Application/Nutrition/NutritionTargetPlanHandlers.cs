using System.Globalization;
using LifeOS.Domain.Nutrition;

namespace LifeOS.Application.Nutrition;

// NUT-003 use cases: nutrition target planning. The user defines bounded periods with a weekly
// pattern and per-date overrides; resolution is deterministic. Nothing here estimates, recommends or
// infers a target, and nothing reads Gym data.

public enum NutritionTargetStatus
{
    Ok,
    Invalid,
    NotFound,
    Overlap
}

public sealed record NutritionTargetInput(decimal? CaloriesKcal, decimal? ProteinGrams, decimal? CarbsGrams, decimal? FatGrams);

public sealed record NutritionTargetDayRuleInput(DayOfWeek Weekday, NutritionTargetDayMode Mode, NutritionTargetInput? Target);

public sealed record NutritionTargetPlanCommand(DateOnly StartsOn, DateOnly EndsOn, NutritionTargetInput? DefaultTarget,
    IReadOnlyList<NutritionTargetDayRuleInput> WeeklyRules);

public sealed record NutritionTargetPlanResult(NutritionTargetStatus Status, NutritionTargetPlan? Plan = null, string? Field = null,
    string? Message = null)
{
    public static NutritionTargetPlanResult Invalid(string field, string message) => new(NutritionTargetStatus.Invalid, Field: field, Message: message);

    public static NutritionTargetPlanResult Overlap(NutritionTargetPlan? conflict) =>
        new(NutritionTargetStatus.Overlap, Field: "period", Message: NutritionTargetPeriods.OverlapMessage(conflict));
}

// The target for one diary date, resolved, plus what the day editor needs: whether a plan covers the
// date and the date's override. Never the whole plan.
public sealed record ResolvedNutritionTarget(DateOnly Date, NutritionTargetValues? Target, bool CoveredByPlan,
    NutritionTargetOverride? Override);

public sealed record ResolvedNutritionTargetResult(NutritionTargetStatus Status, ResolvedNutritionTarget? Resolved = null,
    string? Field = null, string? Message = null)
{
    public static ResolvedNutritionTargetResult Invalid(string field, string message) =>
        new(NutritionTargetStatus.Invalid, Field: field, Message: message);
}

public static class NutritionTargetPeriods
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    // "7 Oct – 3 Nov 2026", or "2 Dec 2026 – 5 Jan 2027" across years.
    public static string Label(DateOnly startsOn, DateOnly endsOn) => startsOn.Year == endsOn.Year
        ? $"{startsOn.ToString("d MMM", Culture)} – {endsOn.ToString("d MMM yyyy", Culture)}"
        : $"{startsOn.ToString("d MMM yyyy", Culture)} – {endsOn.ToString("d MMM yyyy", Culture)}";

    public static string OverlapMessage(NutritionTargetPlan? conflict) => conflict is null
        ? "This period overlaps another target period."
        : $"This period overlaps {Label(conflict.StartsOn, conflict.EndsOn)}.";

    internal static NutritionTargetValues? Values(NutritionTargetInput? input) =>
        input is null || (input.CaloriesKcal is null && input.ProteinGrams is null && input.CarbsGrams is null && input.FatGrams is null)
            ? null
            : NutritionTargetValues.Create(input.CaloriesKcal, input.ProteinGrams, input.CarbsGrams, input.FatGrams);

    // ArgumentException appends " (Parameter '...')" to the message.
    internal static string Message(ArgumentException exception)
    {
        var index = exception.Message.IndexOf(" (Parameter '", StringComparison.Ordinal);

        return index < 0 ? exception.Message : exception.Message[..index];
    }

    // Validation of values inside a section names that section, e.g. "defaultTarget.caloriesKcal".
    internal static string Field(string section, ArgumentException exception) =>
        exception.ParamName is { } name ? $"{section}.{name}" : section;
}

public sealed class GetNutritionTargetPlansHandler(INutritionTargetPlanRepository repository)
{
    public Task<IReadOnlyList<NutritionTargetPlan>> HandleAsync(Guid userId, CancellationToken cancellationToken) =>
        repository.ListAsync(userId, cancellationToken);
}

public sealed class GetNutritionTargetPlanHandler(INutritionTargetPlanRepository repository)
{
    public async Task<NutritionTargetPlanResult> HandleAsync(Guid userId, Guid planId, CancellationToken cancellationToken) =>
        await repository.GetAsync(userId, planId, cancellationToken) is { } plan
            ? new(NutritionTargetStatus.Ok, plan)
            : new(NutritionTargetStatus.NotFound);
}

// Create or edit a plan. Overlaps are rejected (never resolved by moving, shortening or merging
// another plan): first by a readable check, and again by the database if a concurrent save won.
public sealed class SaveNutritionTargetPlanHandler(INutritionTargetPlanRepository repository, TimeProvider clock)
{
    public async Task<NutritionTargetPlanResult> CreateAsync(Guid userId, NutritionTargetPlanCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuild(command, out var defaultTarget, out var rules, out var invalid))
        {
            return invalid!;
        }

        NutritionTargetPlan plan;

        try
        {
            plan = NutritionTargetPlan.Create(userId, command.StartsOn, command.EndsOn, defaultTarget, rules, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return NutritionTargetPlanResult.Invalid(exception.ParamName ?? "plan", NutritionTargetPeriods.Message(exception));
        }

        if (await repository.FindOverlapAsync(userId, plan.StartsOn, plan.EndsOn, null, cancellationToken) is { } conflict)
        {
            return NutritionTargetPlanResult.Overlap(conflict);
        }

        var saved = await repository.AddAsync(plan, cancellationToken);

        return saved.Status == NutritionTargetPlanSaveStatus.Saved
            ? new(NutritionTargetStatus.Ok, plan)
            : NutritionTargetPlanResult.Overlap(saved.Conflict);
    }

    public async Task<NutritionTargetPlanResult> UpdateAsync(Guid userId, Guid planId, NutritionTargetPlanCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryBuild(command, out var defaultTarget, out var rules, out var invalid))
        {
            return invalid!;
        }

        if (await repository.GetAsync(userId, planId, cancellationToken) is not { } plan)
        {
            return new(NutritionTargetStatus.NotFound);
        }

        try
        {
            plan.Update(command.StartsOn, command.EndsOn, defaultTarget, rules, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return NutritionTargetPlanResult.Invalid(exception.ParamName ?? "plan", NutritionTargetPeriods.Message(exception));
        }

        if (await repository.FindOverlapAsync(userId, plan.StartsOn, plan.EndsOn, plan.Id, cancellationToken) is { } conflict)
        {
            return NutritionTargetPlanResult.Overlap(conflict);
        }

        var saved = await repository.UpdateAsync(plan, cancellationToken);

        return saved.Status switch
        {
            NutritionTargetPlanSaveStatus.Saved => new(NutritionTargetStatus.Ok, plan),
            NutritionTargetPlanSaveStatus.NotFound => new(NutritionTargetStatus.NotFound),
            _ => NutritionTargetPlanResult.Overlap(saved.Conflict)
        };
    }

    // Values are validated per section, so a failure names e.g. "weeklyRules.Monday.caloriesKcal".
    private static bool TryBuild(NutritionTargetPlanCommand command, out NutritionTargetValues? defaultTarget,
        out IReadOnlyCollection<NutritionTargetDayRuleSpec> rules, out NutritionTargetPlanResult? invalid)
    {
        defaultTarget = null;
        rules = [];
        invalid = null;

        try
        {
            defaultTarget = NutritionTargetPeriods.Values(command.DefaultTarget);
        }
        catch (ArgumentException exception)
        {
            invalid = NutritionTargetPlanResult.Invalid(NutritionTargetPeriods.Field("defaultTarget", exception), NutritionTargetPeriods.Message(exception));
            return false;
        }

        var specs = new List<NutritionTargetDayRuleSpec>();

        foreach (var rule in command.WeeklyRules ?? [])
        {
            try
            {
                specs.Add(new(rule.Weekday, rule.Mode, NutritionTargetPeriods.Values(rule.Target)));
            }
            catch (ArgumentException exception)
            {
                invalid = NutritionTargetPlanResult.Invalid(NutritionTargetPeriods.Field($"weeklyRules.{rule.Weekday}", exception),
                    $"{rule.Weekday}: {NutritionTargetPeriods.Message(exception)}");
                return false;
            }
        }

        rules = specs;

        return true;
    }
}

// Deleting a plan removes its weekly rules and overrides; its dates then have no target. Other plans
// and all meal data are untouched.
public sealed class DeleteNutritionTargetPlanHandler(INutritionTargetPlanRepository repository)
{
    public Task<bool> HandleAsync(Guid userId, Guid planId, CancellationToken cancellationToken) =>
        repository.DeleteAsync(userId, planId, cancellationToken);
}

public sealed class GetResolvedNutritionTargetHandler(INutritionTargetPlanRepository repository)
{
    public async Task<ResolvedNutritionTargetResult> HandleAsync(Guid userId, DateOnly date, CancellationToken cancellationToken)
    {
        if (!NutritionDays.IsCalendarDate(date))
        {
            return ResolvedNutritionTargetResult.Invalid("date", NutritionDays.InvalidDateMessage);
        }

        return new(NutritionTargetStatus.Ok, Resolve(date, await repository.GetDayAsync(userId, date, cancellationToken)));
    }

    internal static ResolvedNutritionTarget Resolve(DateOnly date, NutritionTargetDay day) =>
        new(date, NutritionTargetResolution.Resolve(date, day.Plan, day.Override), day.Plan is not null, day.Override);
}

// "Edit for this day": a custom target or no target for one date, only inside a plan.
public sealed class SetNutritionTargetOverrideHandler(INutritionTargetPlanRepository repository, TimeProvider clock)
{
    public const string OutsidePlanMessage = "No target period covers this date. Create a period first.";

    public async Task<ResolvedNutritionTargetResult> HandleAsync(Guid userId, DateOnly date, NutritionTargetOverrideMode mode,
        NutritionTargetInput? target, CancellationToken cancellationToken)
    {
        if (!NutritionDays.IsCalendarDate(date))
        {
            return ResolvedNutritionTargetResult.Invalid("date", NutritionDays.InvalidDateMessage);
        }

        var day = await repository.GetDayAsync(userId, date, cancellationToken);

        if (day.Plan is not { } plan)
        {
            return ResolvedNutritionTargetResult.Invalid("date", OutsidePlanMessage);
        }

        NutritionTargetOverride dailyOverride;

        try
        {
            dailyOverride = NutritionTargetOverride.Create(plan, date, mode, NutritionTargetPeriods.Values(target), clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            var field = exception.ParamName is "caloriesKcal" or "proteinGrams" or "carbsGrams" or "fatGrams"
                ? $"target.{exception.ParamName}"
                : exception.ParamName ?? "target";

            return ResolvedNutritionTargetResult.Invalid(field, NutritionTargetPeriods.Message(exception));
        }

        // The plan was deleted or moved meanwhile.
        if (!await repository.SetOverrideAsync(dailyOverride, cancellationToken))
        {
            return ResolvedNutritionTargetResult.Invalid("date", OutsidePlanMessage);
        }

        return new(NutritionTargetStatus.Ok,
            GetResolvedNutritionTargetHandler.Resolve(date, await repository.GetDayAsync(userId, date, cancellationToken)));
    }
}

// Back to the weekday rule. Idempotent.
public sealed class RemoveNutritionTargetOverrideHandler(INutritionTargetPlanRepository repository)
{
    public async Task<ResolvedNutritionTargetResult> HandleAsync(Guid userId, DateOnly date, CancellationToken cancellationToken)
    {
        if (!NutritionDays.IsCalendarDate(date))
        {
            return ResolvedNutritionTargetResult.Invalid("date", NutritionDays.InvalidDateMessage);
        }

        await repository.RemoveOverrideAsync(userId, date, cancellationToken);

        return new(NutritionTargetStatus.Ok,
            GetResolvedNutritionTargetHandler.Resolve(date, await repository.GetDayAsync(userId, date, cancellationToken)));
    }
}
