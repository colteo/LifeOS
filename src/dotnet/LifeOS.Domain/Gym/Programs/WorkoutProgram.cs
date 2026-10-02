namespace LifeOS.Domain.Gym.Programs;

// A user's workout program, as supplied by their trainer: an ordered list of workouts. The program is
// the aggregate root; its workouts, blocks, exercise prescriptions and set prescriptions belong to it
// and are deleted with it. Exercises are separate, reusable entities and are only referenced.
public sealed class WorkoutProgram
{
    private readonly List<WorkoutTemplate> _workouts = [];

    private WorkoutProgram(Guid id, Guid userId, string name, DateTimeOffset createdAtUtc)
    {
        Id = id;
        UserId = userId;
        Name = name;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    // The owning LifeOS user.
    public Guid UserId { get; }

    public string Name { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    // Ordered by position (1, 2, 3, ...). Workouts are not bound to weekdays: order is all that matters.
    public IReadOnlyList<WorkoutTemplate> Workouts => _workouts.OrderBy(workout => workout.Position).ToList();

    public static WorkoutProgram Create(Guid userId, string name, DateTimeOffset createdAtUtc)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A valid user id is required.", nameof(userId));
        }

        return new WorkoutProgram(Guid.CreateVersion7(), userId, NormalizeName(name), createdAtUtc.ToUniversalTime());
    }

    public void Rename(string name)
    {
        Name = NormalizeName(name);
    }

    public WorkoutTemplate? FindWorkout(Guid workoutId) =>
        _workouts.SingleOrDefault(workout => workout.Id == workoutId);

    // Appended after the last workout.
    public WorkoutTemplate AddWorkout(string name)
    {
        var workout = WorkoutTemplate.Create(this, name, _workouts.Count + 1);
        _workouts.Add(workout);

        return workout;
    }

    // Removes the workout with everything it contains; the remaining workouts close the gap.
    public bool RemoveWorkout(Guid workoutId)
    {
        var workout = FindWorkout(workoutId);

        if (workout is null)
        {
            return false;
        }

        _workouts.Remove(workout);
        Positions.Renumber(Workouts, (item, position) => item.MoveTo(position));

        return true;
    }

    // orderedWorkoutIds must list every workout of the program exactly once, in the new order.
    public void ReorderWorkouts(IReadOnlyList<Guid> orderedWorkoutIds)
    {
        var ordered = Positions.Arrange(_workouts, workout => workout.Id, orderedWorkoutIds, "workoutIds", "workout");
        Positions.Renumber(ordered, (item, position) => item.MoveTo(position));
    }

    internal static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return name.Trim();
    }
}
