using System.Reflection;
using LifeOS.Application.Habits;
using LifeOS.Domain.Habits;

namespace LifeOS.UnitTests.Fakes;

// Every operation filters by userId, like the EF Core repository; ownership tests depend on it. Habits are
// stored and returned as copies (schedule included), so a caller's unsaved changes never leak into the
// stored state.
internal sealed class InMemoryHabitRepository : IHabitRepository
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo ScheduleField =
        typeof(Habit).GetField("_schedule", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Lock _lock = new();

    public List<Habit> Habits { get; } = [];

    public Dictionary<(Guid HabitId, DateOnly Date), HabitCheckIn> CheckIns { get; } = [];

    // Calls of GetCheckInsAsync with their (from, to) range.
    public List<(DateOnly From, DateOnly To)> CheckInReads { get; } = [];

    public Task<int> CountActiveAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Habits.Count(habit => habit.UserId == userId && habit.Status == HabitStatus.Active));
        }
    }

    public Task<bool> HasActiveHydrationHabitAsync(Guid userId, Guid? exceptHabitId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Habits.Any(habit => habit.UserId == userId && habit.Id != exceptHabitId && IsActiveWater(habit)));
        }
    }

    // Like ux_habits_user_active_hydration: a second active water habit is a conflict.
    public Task AddAsync(Habit habit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            RequireOneActiveWaterHabit(habit);
            Habits.Add(Clone(habit));
        }

        return Task.CompletedTask;
    }

    public Task<Habit?> GetAsync(Guid userId, Guid habitId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Habits.SingleOrDefault(habit => habit.UserId == userId && habit.Id == habitId) is { } habit ? Clone(habit) : null);
        }
    }

    public Task<IReadOnlyList<Habit>> GetAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Habit>>(Habits
                .Where(habit => habit.UserId == userId)
                .OrderBy(habit => habit.CreatedAtUtc)
                .ThenBy(habit => habit.Id)
                .Select(Clone)
                .ToList());
        }
    }

    public Task<bool> SaveAsync(Habit habit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = Habits.FindIndex(stored => stored.UserId == habit.UserId && stored.Id == habit.Id);

            if (index < 0)
            {
                return Task.FromResult(false);
            }

            RequireOneActiveWaterHabit(habit);
            Habits[index] = Clone(habit);
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(Guid userId, Guid habitId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (Habits.RemoveAll(habit => habit.UserId == userId && habit.Id == habitId) == 0)
            {
                return Task.FromResult(false);
            }

            foreach (var key in CheckIns.Keys.Where(key => key.HabitId == habitId).ToList())
            {
                CheckIns.Remove(key);
            }

            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<HabitCheckIn>> GetCheckInsAsync(Guid userId, IReadOnlyCollection<Guid> habitIds, DateOnly from, DateOnly to,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            CheckInReads.Add((from, to));

            return Task.FromResult<IReadOnlyList<HabitCheckIn>>(CheckIns.Values
                .Where(checkIn => checkIn.UserId == userId && habitIds.Contains(checkIn.HabitId) && checkIn.Date >= from && checkIn.Date <= to)
                .OrderBy(checkIn => checkIn.Date)
                .ToList());
        }
    }

    public Task<bool> UpsertCheckInAsync(HabitCheckIn checkIn, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!Habits.Any(habit => habit.UserId == checkIn.UserId && habit.Id == checkIn.HabitId))
            {
                return Task.FromResult(false);
            }

            CheckIns[(checkIn.HabitId, checkIn.Date)] = checkIn;
            return Task.FromResult(true);
        }
    }

    public Task<bool> RemoveCheckInAsync(Guid userId, Guid habitId, DateOnly date, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(CheckIns.TryGetValue((habitId, date), out var checkIn) && checkIn.UserId == userId
                && CheckIns.Remove((habitId, date)));
        }
    }

    private void RequireOneActiveWaterHabit(Habit habit)
    {
        if (IsActiveWater(habit) && Habits.Any(stored => stored.UserId == habit.UserId && stored.Id != habit.Id && IsActiveWater(stored)))
        {
            throw new HabitConflictException(HabitConflictException.HydrationHabitExists, "You already have a water habit. Archive it first.");
        }
    }

    private static bool IsActiveWater(Habit habit) =>
        habit is { CompletionSource: HabitCompletionSource.Hydration, Status: HabitStatus.Active };

    private static Habit Clone(Habit habit)
    {
        var copy = (Habit)CloneMethod.Invoke(habit, null)!;
        var versions = ((List<HabitScheduleVersion>)ScheduleField.GetValue(habit)!)
            .Select(version => (HabitScheduleVersion)CloneMethod.Invoke(version, null)!)
            .ToList();

        ScheduleField.SetValue(copy, versions);
        return copy;
    }
}
