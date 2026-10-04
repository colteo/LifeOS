using LifeOS.Domain.Nutrition;

namespace LifeOS.Application.Nutrition;

// NUT-003 use cases: manual daily targets. The user decides the values; nothing here estimates,
// recommends or derives a target. Setting or removing always applies from the user's local today
// (from the server clock and the device's UTC offset): the client never chooses the effective date, so
// past days keep the target that applied to them.

// The target that applies on a day: its values and the date that state took effect.
public sealed record EffectiveNutritionTarget(DateOnly EffectiveFrom, NutritionTargetValues Values, NutritionTargetSource Source)
{
    // Null when there is no state yet or the latest state is "no targets".
    public static EffectiveNutritionTarget? From(NutritionTarget? state) =>
        state?.Values is { } values ? new(state.EffectiveFrom, values, state.Source) : null;
}

// Date: the day the target was resolved for (the user's local today for the "current" use cases).
public sealed record NutritionTargetResult(NutritionStatus Status, DateOnly Date = default, EffectiveNutritionTarget? Target = null,
    string? Field = null, string? Message = null)
{
    public static NutritionTargetResult Invalid(string field, string message) => new(NutritionStatus.Invalid, Field: field, Message: message);
}

// Null metrics are not targeted; at least one must be present (no targets is RemoveNutritionTargetHandler).
public sealed record SetNutritionTargetCommand(decimal? CaloriesKcal, decimal? ProteinGrams, decimal? CarbsGrams, decimal? FatGrams,
    int UtcOffsetMinutes);

// The target that applied on any diary day, past or present.
public sealed class GetNutritionTargetHandler(INutritionTargetRepository repository)
{
    public async Task<NutritionTargetResult> HandleAsync(Guid userId, DateOnly date, CancellationToken cancellationToken)
    {
        if (!NutritionDays.IsCalendarDate(date))
        {
            return NutritionTargetResult.Invalid("date", NutritionDays.InvalidDateMessage);
        }

        return new(NutritionStatus.Ok, date, EffectiveNutritionTarget.From(await repository.GetEffectiveAsync(userId, date, cancellationToken)));
    }
}

// The target that applies on the user's local today.
public sealed class GetCurrentNutritionTargetHandler(INutritionTargetRepository repository, TimeProvider clock)
{
    public async Task<NutritionTargetResult> HandleAsync(Guid userId, int utcOffsetMinutes, CancellationToken cancellationToken)
    {
        if (!NutritionDays.TryGetLocalToday(clock, utcOffsetMinutes, out var today))
        {
            return NutritionTargetResult.Invalid("utcOffsetMinutes", NutritionDays.InvalidOffsetMessage);
        }

        return new(NutritionStatus.Ok, today, EffectiveNutritionTarget.From(await repository.GetEffectiveAsync(userId, today, cancellationToken)));
    }
}

// Set or edit targets from today on. Saving again on the same day replaces today's state; earlier
// days are untouched.
public sealed class SetNutritionTargetHandler(INutritionTargetRepository repository, TimeProvider clock)
{
    public async Task<NutritionTargetResult> HandleAsync(Guid userId, SetNutritionTargetCommand command, CancellationToken cancellationToken)
    {
        if (!NutritionDays.TryGetLocalToday(clock, command.UtcOffsetMinutes, out var today))
        {
            return NutritionTargetResult.Invalid("utcOffsetMinutes", NutritionDays.InvalidOffsetMessage);
        }

        NutritionTarget target;

        try
        {
            var values = NutritionTargetValues.Create(command.CaloriesKcal, command.ProteinGrams, command.CarbsGrams, command.FatGrams);
            target = NutritionTarget.Set(userId, today, values, NutritionTargetSource.Manual, clock.GetUtcNow());
        }
        catch (ArgumentException exception)
        {
            return NutritionTargetResult.Invalid(exception.ParamName ?? "target", FirstLine(exception.Message));
        }

        await repository.SaveAsync(target, cancellationToken);

        return new(NutritionStatus.Ok, today, EffectiveNutritionTarget.From(target));
    }

    // ArgumentException appends " (Parameter '...')" to the message.
    private static string FirstLine(string message)
    {
        var index = message.IndexOf(" (Parameter '", StringComparison.Ordinal);

        return index < 0 ? message : message[..index];
    }
}

// Remove targets: from today on there are none. Earlier states stay and keep applying to their days.
// With nothing active today, nothing is written.
public sealed class RemoveNutritionTargetHandler(INutritionTargetRepository repository, TimeProvider clock)
{
    public async Task<NutritionTargetResult> HandleAsync(Guid userId, int utcOffsetMinutes, CancellationToken cancellationToken)
    {
        if (!NutritionDays.TryGetLocalToday(clock, utcOffsetMinutes, out var today))
        {
            return NutritionTargetResult.Invalid("utcOffsetMinutes", NutritionDays.InvalidOffsetMessage);
        }

        if (EffectiveNutritionTarget.From(await repository.GetEffectiveAsync(userId, today, cancellationToken)) is not null)
        {
            await repository.SaveAsync(NutritionTarget.Remove(userId, today, NutritionTargetSource.Manual, clock.GetUtcNow()), cancellationToken);
        }

        return new(NutritionStatus.Ok, today);
    }
}
