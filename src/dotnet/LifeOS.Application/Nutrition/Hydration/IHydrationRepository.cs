using LifeOS.Domain.Nutrition.Hydration;

namespace LifeOS.Application.Nutrition.Hydration;

// Hydration's own persistence (HYD-001 §7.1). Every method is scoped to userId: another user's intake
// is indistinguishable from a missing one.
public interface IHydrationRepository
{
    Task AddAsync(HydrationIntake intake, CancellationToken cancellationToken);

    Task<HydrationIntake?> GetAsync(Guid userId, Guid intakeId, CancellationToken cancellationToken);

    // Saves the amount and time of an intake read through GetAsync. False when it no longer exists.
    Task<bool> SaveAsync(HydrationIntake intake, CancellationToken cancellationToken);

    // False when the user has no intake with this id.
    Task<bool> DeleteAsync(Guid userId, Guid intakeId, CancellationToken cancellationToken);

    // One diary day, newest first (diary time, then id, descending).
    Task<IReadOnlyList<HydrationIntake>> GetForDateAsync(Guid userId, DateOnly date, CancellationToken cancellationToken);

    // SUM(amount) per diary date with from <= date <= to; dates without intakes are absent.
    Task<IReadOnlyDictionary<DateOnly, int>> GetDailyTotalsAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
