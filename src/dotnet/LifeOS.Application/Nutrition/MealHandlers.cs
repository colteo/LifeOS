using LifeOS.Domain.Nutrition;

namespace LifeOS.Application.Nutrition;

public sealed record CreateMealCommand(string Description, MealType? MealType, DateOnly DiaryDate, TimeOnly Time, int UtcOffsetMinutes);

// ClearNutrition confirms that a description change may remove the meal's nutrition (NUT-002).
public sealed record UpdateMealCommand(string Description, MealType? MealType, TimeOnly Time, bool ClearNutrition = false);

public sealed record MealsForDateResult(MealResultStatus Status, IReadOnlyList<MealEntrySummary> Meals, string? Message = null);

// One diary day of the journal, newest first, each meal with its current nutrition (NUT-002). The
// order is the journal's rule, so it is applied here as well as by the repository's index-backed query.
public sealed class GetMealsForDateHandler(IMealNutritionRepository repository)
{
    public async Task<MealsForDateResult> HandleAsync(Guid userId, DateOnly diaryDate, CancellationToken cancellationToken)
    {
        if (diaryDate == DateOnly.MinValue || diaryDate == DateOnly.MaxValue)
        {
            return new(MealResultStatus.Invalid, [], "Choose a calendar date between 0001-01-02 and 9999-12-30.");
        }

        var day = await repository.GetDayAsync(userId, diaryDate, cancellationToken);

        return new(MealResultStatus.Ok, day
            .OrderByDescending(meal => meal.Meal.DiaryTime)
            .ThenByDescending(meal => meal.Meal.Id)
            .Select(MealEntrySummary.From)
            .ToList());
    }

    public static IEnumerable<MealEntry> NewestFirst(IEnumerable<MealEntry> entries) =>
        entries.OrderByDescending(entry => entry.DiaryTime).ThenByDescending(entry => entry.Id);
}

public sealed class CreateMealHandler(IMealEntryRepository repository, TimeProvider clock)
{
    public async Task<MealResult> HandleAsync(Guid userId, CreateMealCommand command, CancellationToken cancellationToken)
    {
        MealEntry entry;

        try
        {
            entry = MealEntry.Create(userId, command.Description, command.MealType, command.DiaryDate, command.Time,
                command.UtcOffsetMinutes, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return MealResult.Invalid(exception);
        }

        await repository.AddAsync(entry, cancellationToken);

        return MealResult.Ok(entry);
    }
}

// Edits the text, type and time; the meal stays on its diary day (NUT-001).
//
// NUT-002: a snapshot describes the description it was made for. A changed description therefore
// removes the meal's nutrition in the same transaction as the update; when the meal has nutrition the
// caller must confirm that first (ClearNutrition), otherwise nothing changes. Changing only the time or
// meal type keeps the nutrition.
public sealed class UpdateMealHandler(IMealEntryRepository repository, IMealNutritionRepository nutrition, TimeProvider clock)
{
    public async Task<MealResult> HandleAsync(Guid userId, Guid id, UpdateMealCommand command, CancellationToken cancellationToken)
    {
        var current = await nutrition.GetMealAsync(userId, id, cancellationToken);

        if (current is null)
        {
            return MealResult.NotFound();
        }

        var entry = current.Meal;
        var previousDescription = entry.Description;

        try
        {
            entry.Update(command.Description, command.MealType, command.Time, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return MealResult.Invalid(exception);
        }

        var descriptionChanged = !string.Equals(entry.Description, previousDescription, StringComparison.Ordinal);

        if (descriptionChanged && current.Nutrition is not null && !command.ClearNutrition)
        {
            return MealResult.NutritionClearRequired();
        }

        if (!await repository.UpdateAsync(entry, clearNutrition: descriptionChanged, cancellationToken))
        {
            return MealResult.NotFound();
        }

        var updated = await nutrition.GetMealAsync(userId, id, cancellationToken);

        return updated is null ? MealResult.NotFound() : MealResult.Ok(updated.Meal, updated.Nutrition);
    }
}

public sealed class DeleteMealHandler(IMealEntryRepository repository)
{
    public async Task<MealResultStatus> HandleAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        await repository.DeleteAsync(userId, id, cancellationToken) ? MealResultStatus.Ok : MealResultStatus.NotFound;
}
