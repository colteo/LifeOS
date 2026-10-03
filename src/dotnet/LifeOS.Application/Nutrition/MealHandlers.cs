using LifeOS.Domain.Nutrition;

namespace LifeOS.Application.Nutrition;

public sealed record CreateMealCommand(string Description, MealType? MealType, DateOnly DiaryDate, TimeOnly Time, int UtcOffsetMinutes);

public sealed record UpdateMealCommand(string Description, MealType? MealType, TimeOnly Time);

public sealed record MealsForDateResult(MealResultStatus Status, IReadOnlyList<MealEntrySummary> Meals, string? Message = null);

// One diary day of the journal, newest first. The order is the journal's rule, so it is applied here
// as well as by the repository's index-backed query.
public sealed class GetMealsForDateHandler(IMealEntryRepository repository)
{
    public async Task<MealsForDateResult> HandleAsync(Guid userId, DateOnly diaryDate, CancellationToken cancellationToken)
    {
        if (diaryDate == DateOnly.MinValue || diaryDate == DateOnly.MaxValue)
        {
            return new(MealResultStatus.Invalid, [], "Choose a calendar date between 0001-01-02 and 9999-12-30.");
        }

        var entries = await repository.GetForDiaryDateAsync(userId, diaryDate, cancellationToken);

        return new(MealResultStatus.Ok, NewestFirst(entries).Select(MealEntrySummary.From).ToList());
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
public sealed class UpdateMealHandler(IMealEntryRepository repository, TimeProvider clock)
{
    public async Task<MealResult> HandleAsync(Guid userId, Guid id, UpdateMealCommand command, CancellationToken cancellationToken)
    {
        var entry = await repository.GetAsync(userId, id, cancellationToken);

        if (entry is null)
        {
            return MealResult.NotFound();
        }

        try
        {
            entry.Update(command.Description, command.MealType, command.Time, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return MealResult.Invalid(exception);
        }

        return await repository.UpdateAsync(entry, cancellationToken) ? MealResult.Ok(entry) : MealResult.NotFound();
    }
}

public sealed class DeleteMealHandler(IMealEntryRepository repository)
{
    public async Task<MealResultStatus> HandleAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        await repository.DeleteAsync(userId, id, cancellationToken) ? MealResultStatus.Ok : MealResultStatus.NotFound;
}
