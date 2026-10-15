using LifeOS.Application.Nutrition.Hydration;
using LifeOS.Domain.Nutrition.Hydration;

namespace LifeOS.UnitTests.Fakes;

// HYD-001 repository in memory, scoped by user like the PostgreSQL one.
internal sealed class InMemoryHydrationRepository : IHydrationRepository
{
    private readonly List<HydrationIntake> _intakes = [];

    public IReadOnlyList<HydrationIntake> Intakes => _intakes;

    public Task AddAsync(HydrationIntake intake, CancellationToken cancellationToken)
    {
        _intakes.Add(intake);
        return Task.CompletedTask;
    }

    public Task<HydrationIntake?> GetAsync(Guid userId, Guid intakeId, CancellationToken cancellationToken) =>
        Task.FromResult(_intakes.SingleOrDefault(intake => intake.UserId == userId && intake.Id == intakeId));

    // The stored instance is the one the handler changed.
    public Task<bool> SaveAsync(HydrationIntake intake, CancellationToken cancellationToken) =>
        Task.FromResult(_intakes.Contains(intake));

    public Task<bool> DeleteAsync(Guid userId, Guid intakeId, CancellationToken cancellationToken) =>
        Task.FromResult(_intakes.RemoveAll(intake => intake.UserId == userId && intake.Id == intakeId) == 1);

    public Task<IReadOnlyList<HydrationIntake>> GetForDateAsync(Guid userId, DateOnly date, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HydrationIntake>>(_intakes
            .Where(intake => intake.UserId == userId && intake.DiaryDate == date)
            .OrderByDescending(intake => intake.DiaryTime)
            .ThenByDescending(intake => intake.Id)
            .ToList());

    public Task<IReadOnlyDictionary<DateOnly, int>> GetDailyTotalsAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<DateOnly, int>>(_intakes
            .Where(intake => intake.UserId == userId && intake.DiaryDate >= from && intake.DiaryDate <= to)
            .GroupBy(intake => intake.DiaryDate)
            .ToDictionary(group => group.Key, group => group.Sum(intake => intake.AmountMl)));
}
