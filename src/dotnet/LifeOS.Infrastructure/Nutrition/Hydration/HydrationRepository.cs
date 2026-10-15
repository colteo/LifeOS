using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Domain.Nutrition.Hydration;
using LifeOS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Nutrition.Hydration;

// HYD-001 on PostgreSQL. Every query filters by user_id; a foreign id behaves exactly like a missing one.
internal sealed class HydrationRepository(LifeOSDbContext dbContext) : IHydrationRepository
{
    public async Task AddAsync(HydrationIntake intake, CancellationToken cancellationToken)
    {
        dbContext.HydrationIntakes.Add(intake);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.Entry(intake).State = EntityState.Detached;
    }

    public Task<HydrationIntake?> GetAsync(Guid userId, Guid intakeId, CancellationToken cancellationToken) =>
        dbContext.HydrationIntakes
            .AsNoTracking()
            .SingleOrDefaultAsync(intake => intake.UserId == userId && intake.Id == intakeId, cancellationToken);

    // The diary date, owner and creation time never change, so only the edited columns are written.
    public async Task<bool> SaveAsync(HydrationIntake intake, CancellationToken cancellationToken) =>
        await dbContext.HydrationIntakes
            .Where(stored => stored.UserId == intake.UserId && stored.Id == intake.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(stored => stored.AmountMl, intake.AmountMl)
                .SetProperty(stored => stored.DiaryTime, intake.DiaryTime)
                .SetProperty(stored => stored.OccurredAtUtc, intake.OccurredAtUtc)
                .SetProperty(stored => stored.UpdatedAtUtc, intake.UpdatedAtUtc), cancellationToken) == 1;

    public async Task<bool> DeleteAsync(Guid userId, Guid intakeId, CancellationToken cancellationToken) =>
        await dbContext.HydrationIntakes
            .Where(intake => intake.UserId == userId && intake.Id == intakeId)
            .ExecuteDeleteAsync(cancellationToken) == 1;

    public async Task<IReadOnlyList<HydrationIntake>> GetForDateAsync(Guid userId, DateOnly date, CancellationToken cancellationToken) =>
        await dbContext.HydrationIntakes
            .AsNoTracking()
            .Where(intake => intake.UserId == userId && intake.DiaryDate == date)
            .OrderByDescending(intake => intake.DiaryTime)
            .ThenByDescending(intake => intake.Id)
            .ToListAsync(cancellationToken);

    // One GROUP BY diary_date over ix_hydration_intakes_user_diary.
    public async Task<IReadOnlyDictionary<DateOnly, int>> GetDailyTotalsAsync(Guid userId, DateOnly from, DateOnly to,
        CancellationToken cancellationToken) =>
        await dbContext.HydrationIntakes
            .AsNoTracking()
            .Where(intake => intake.UserId == userId && intake.DiaryDate >= from && intake.DiaryDate <= to)
            .GroupBy(intake => intake.DiaryDate)
            .Select(group => new { Date = group.Key, TotalMl = group.Sum(intake => intake.AmountMl) })
            .ToDictionaryAsync(day => day.Date, day => day.TotalMl, cancellationToken);
}
