using LifeOS.Domain.Nutrition;

namespace LifeOS.Application.Nutrition;

// NUT-002 use cases. Writing a meal never estimates anything: AI runs only when the user asks for one
// meal's estimate, when the user analyzes a day, or when a past day is lazily closed. AI estimates each
// meal on its own; daily totals are always the deterministic sum of the persisted snapshots.

public enum NutritionStatus
{
    Ok,
    Invalid,
    NotFound,

    // The estimator is not configured, unreachable, timed out or rate-limited.
    EstimationUnavailable,

    // The estimator answered without a usable estimate for this meal.
    NotEstimable
}

// An AI proposal for one meal: validated values plus the assumptions behind them. Never persisted.
public sealed record MealNutritionProposal(NutritionValues Values, IReadOnlyList<string> Assumptions);

public sealed record MealEstimateResult(NutritionStatus Status, MealNutritionProposal? Proposal = null);

// Daily totals from the CURRENT snapshots of the meals on one diary day, as exact decimal sums. A partial
// total covers only AnalyzedMealCount of MealCount meals and must always be shown with those counts.
public sealed record DailyNutritionSummary(
    DateOnly Date,
    int MealCount,
    int AnalyzedMealCount,
    decimal CaloriesKcal,
    decimal ProteinGrams,
    decimal CarbsGrams,
    decimal FatGrams)
{
    public bool AllAnalyzed => MealCount > 0 && AnalyzedMealCount == MealCount;

    public static DailyNutritionSummary From(DateOnly date, IReadOnlyCollection<MealWithNutrition> day)
    {
        var values = day.Where(meal => meal.Nutrition is not null).Select(meal => meal.Nutrition!.Values).ToList();

        return new(date, day.Count, values.Count,
            values.Sum(value => value.CaloriesKcal),
            values.Sum(value => value.ProteinGrams),
            values.Sum(value => value.CarbsGrams),
            values.Sum(value => value.FatGrams));
    }
}

// The outcome of a bulk run (Analyze day, lazy close). Successes are kept even when other meals fail.
// MorePending: meals that this run left without nutrition (failures, the per-run bound, or estimation
// becoming unavailable).
public sealed record NutritionAnalysisResult(int Analyzed, int Failed, bool EstimationUnavailable, bool MorePending);

public sealed record AnalyzeDayResult(NutritionStatus Status, NutritionAnalysisResult? Analysis = null,
    DailyNutritionSummary? Summary = null, string? Message = null);

public sealed record LazyCloseResult(NutritionStatus Status, NutritionAnalysisResult? Analysis = null, string? Field = null,
    string? Message = null);

public sealed record SetMealNutritionCommand(decimal CaloriesKcal, decimal ProteinGrams, decimal CarbsGrams, decimal FatGrams,
    NutritionSource Source);

// Asks the estimator about one meal's text and type only, and validates its answer. Shared by the
// single-meal estimate and the bulk runs.
public sealed class MealNutritionEstimation(INutritionEstimationService estimator, IMealNutritionRepository repository,
    TimeProvider clock)
{
    public async Task<MealEstimateResult> EstimateAsync(MealEntry meal, CancellationToken cancellationToken)
    {
        var result = await estimator.EstimateAsync(new MealEstimationInput(meal.Description, meal.MealType), cancellationToken);

        if (result.Estimate is not { } estimate)
        {
            return new(result.Failure == NutritionEstimationFailure.NotEstimable
                ? NutritionStatus.NotEstimable
                : NutritionStatus.EstimationUnavailable);
        }

        try
        {
            var values = NutritionValues.Create(estimate.CaloriesKcal, estimate.ProteinGrams, estimate.CarbsGrams, estimate.FatGrams);
            var assumptions = (estimate.Assumptions ?? [])
                .Where(assumption => !string.IsNullOrWhiteSpace(assumption))
                .Select(assumption => assumption.Trim())
                .ToList();

            return new(NutritionStatus.Ok, new MealNutritionProposal(values, assumptions));
        }
        catch (ArgumentException)
        {
            // Out-of-range values are not an estimate LifeOS can store: never clamp or invent numbers.
            return new(NutritionStatus.NotEstimable);
        }
    }

    // Estimates the meals one at a time, in the given order, and inserts each success at once with
    // this source; a later failure never undoes an earlier success. Meals that already have a snapshot
    // (e.g. analyzed concurrently) or that changed meanwhile are left alone. Stops at the first
    // "unavailable": the remaining meals stay pending for a later run.
    public async Task<NutritionAnalysisResult> AnalyzeMissingAsync(IReadOnlyList<MealEntry> meals, NutritionSource source,
        bool moreBeyondThisRun, CancellationToken cancellationToken)
    {
        var analyzed = 0;
        var failed = 0;
        var processed = 0;
        var unavailable = false;

        foreach (var meal in meals)
        {
            var estimate = await EstimateAsync(meal, cancellationToken);

            if (estimate.Status == NutritionStatus.EstimationUnavailable)
            {
                unavailable = true;
                break;
            }

            processed++;

            if (estimate.Proposal is null)
            {
                failed++;
                continue;
            }

            var snapshot = MealNutritionSnapshot.Create(meal.Id, estimate.Proposal.Values, source, clock.GetUtcNow());

            if (await repository.AddIfMissingAsync(meal.UserId, snapshot, meal.Description, cancellationToken))
            {
                analyzed++;
            }
        }

        return new(analyzed, failed, unavailable, moreBeyondThisRun || failed > 0 || processed < meals.Count);
    }
}

// Explicit single-meal estimate: a proposal only. Nothing is persisted; the user confirms, edits or
// cancels it. Re-estimating a meal that has nutrition never changes the current snapshot either.
public sealed class EstimateMealNutritionHandler(IMealEntryRepository meals, MealNutritionEstimation estimation)
{
    public async Task<MealEstimateResult> HandleAsync(Guid userId, Guid mealId, CancellationToken cancellationToken)
    {
        var meal = await meals.GetAsync(userId, mealId, cancellationToken);

        return meal is null
            ? new MealEstimateResult(NutritionStatus.NotFound)
            : await estimation.EstimateAsync(meal, cancellationToken);
    }
}

// Confirm (AiConfirmed: the proposal unchanged) or edit (UserAdjusted) a meal's nutrition. An explicit
// decision, so it replaces the meal's current snapshot. Never calls the estimator.
public sealed class SetMealNutritionHandler(IMealNutritionRepository repository, TimeProvider clock)
{
    public async Task<MealResult> HandleAsync(Guid userId, Guid mealId, SetMealNutritionCommand command, CancellationToken cancellationToken)
    {
        if (!MealNutritionSnapshot.MayReplaceExisting(command.Source))
        {
            return MealResult.Invalid("source", "Source must be AiConfirmed or UserAdjusted.");
        }

        MealNutritionSnapshot snapshot;

        try
        {
            var values = NutritionValues.Create(command.CaloriesKcal, command.ProteinGrams, command.CarbsGrams, command.FatGrams);
            snapshot = MealNutritionSnapshot.Create(mealId, values, command.Source, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return MealResult.Invalid(exception);
        }

        if (!await repository.SaveAsync(userId, snapshot, cancellationToken))
        {
            return MealResult.NotFound();
        }

        var meal = await repository.GetMealAsync(userId, mealId, cancellationToken);

        return meal is null ? MealResult.NotFound() : MealResult.Ok(meal.Meal, meal.Nutrition);
    }
}

public sealed class GetDailyNutritionSummaryHandler(IMealNutritionRepository repository)
{
    public async Task<AnalyzeDayResult> HandleAsync(Guid userId, DateOnly date, CancellationToken cancellationToken)
    {
        if (!NutritionDays.IsCalendarDate(date))
        {
            return new(NutritionStatus.Invalid, Message: NutritionDays.InvalidDateMessage);
        }

        return new(NutritionStatus.Ok, Summary: DailyNutritionSummary.From(date, await repository.GetDayAsync(userId, date, cancellationToken)));
    }
}

// Analyze day / Analyze remaining: estimates the day's meals that have no snapshot (earliest first) and
// stores each success as AiRequested. Existing snapshots are never touched. Bounded per request.
public sealed class AnalyzeDayHandler(IMealNutritionRepository repository, MealNutritionEstimation estimation)
{
    public async Task<AnalyzeDayResult> HandleAsync(Guid userId, DateOnly date, CancellationToken cancellationToken)
    {
        if (!NutritionDays.IsCalendarDate(date))
        {
            return new(NutritionStatus.Invalid, Message: NutritionDays.InvalidDateMessage);
        }

        var missing = (await repository.GetDayAsync(userId, date, cancellationToken))
            .Where(meal => meal.Nutrition is null)
            .Select(meal => meal.Meal)
            .OrderBy(meal => meal.DiaryTime)
            .ThenBy(meal => meal.Id)
            .ToList();

        var analysis = await estimation.AnalyzeMissingAsync(missing.Take(NutritionDays.MaxMealsPerRun).ToList(),
            NutritionSource.AiRequested, missing.Count > NutritionDays.MaxMealsPerRun, cancellationToken);

        var summary = DailyNutritionSummary.From(date, await repository.GetDayAsync(userId, date, cancellationToken));

        return new(NutritionStatus.Ok, analysis, summary);
    }
}

// Lazy daily close: estimates past meals (diary days before the user's local today) that have no
// snapshot and stores each success as AiAutoClosed. Today is never analyzed automatically. At most
// MaxMealsPerRun meals per call, most recent day first; the rest stay pending for the next call.
// Idempotent: a run with nothing pending does nothing. Written as a plain use case so a future
// scheduler can call CloseBeforeAsync directly.
public sealed class LazyCloseNutritionHandler(IMealNutritionRepository repository, MealNutritionEstimation estimation, TimeProvider clock)
{
    public async Task<LazyCloseResult> HandleAsync(Guid userId, int utcOffsetMinutes, CancellationToken cancellationToken)
    {
        if (utcOffsetMinutes is < -MealEntry.MaxUtcOffsetMinutes or > MealEntry.MaxUtcOffsetMinutes)
        {
            return new(NutritionStatus.Invalid, Field: "utcOffsetMinutes", Message: "UTC offset must be between -840 and 840 minutes.");
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromMinutes(utcOffsetMinutes)).DateTime);

        return new(NutritionStatus.Ok, await CloseBeforeAsync(userId, today, cancellationToken));
    }

    public async Task<NutritionAnalysisResult> CloseBeforeAsync(Guid userId, DateOnly today, CancellationToken cancellationToken)
    {
        var pending = await repository.GetUnanalyzedBeforeAsync(userId, today, NutritionDays.MaxMealsPerRun + 1, cancellationToken);

        return await estimation.AnalyzeMissingAsync(pending.Take(NutritionDays.MaxMealsPerRun).ToList(),
            NutritionSource.AiAutoClosed, pending.Count > NutritionDays.MaxMealsPerRun, cancellationToken);
    }
}

public static class NutritionDays
{
    // The most meals one Analyze day or lazy close request estimates (sequentially). Keeps a request's
    // provider calls and duration bounded; the remaining meals stay pending for the next request.
    public const int MaxMealsPerRun = 20;

    public const string InvalidDateMessage = "Choose a calendar date between 0001-01-02 and 9999-12-30.";

    public static bool IsCalendarDate(DateOnly date) => date != DateOnly.MinValue && date != DateOnly.MaxValue;
}
