using LifeOS.Domain.Nutrition.Hydration;

namespace LifeOS.Application.Nutrition.Hydration;

// HYD-001 P-12: the ONLY Hydration surface other modules (Habits, Reminders) may use. Read-only and
// computed on every call: consumers can neither write nor cache water data.
public interface IHydrationDayReader
{
    // One HydrationDay per date in [from, to], inclusive and in date order; empty dates have TotalMl = 0.
    Task<IReadOnlyList<HydrationDay>> GetDaysAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
