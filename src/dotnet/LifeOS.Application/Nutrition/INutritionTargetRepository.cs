using LifeOS.Domain.Nutrition;

namespace LifeOS.Application.Nutrition;

// The user's daily-target history (NUT-003). Every method is scoped to userId. States are never
// deleted; at most one exists per user and effective date.
public interface INutritionTargetRepository
{
    // The latest state with EffectiveFrom on or before date (possibly a "no targets" state), or null
    // when the user has no state that early.
    Task<NutritionTarget?> GetEffectiveAsync(Guid userId, DateOnly date, CancellationToken cancellationToken);

    // Inserts the state for its (user, EffectiveFrom), or replaces that day's state in place (keeping
    // its id and creation time). One atomic statement: concurrent saves for the same day leave one state.
    Task SaveAsync(NutritionTarget target, CancellationToken cancellationToken);
}
