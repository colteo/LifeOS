using LifeOS.Domain.Gym.Programs;

namespace LifeOS.Domain.Gym.Sessions;

// One set of the snapshot: its order and target reps (copied at start), and what was actually done.
// A set is pending until it is recorded; then it has ActualReps, an optional WeightKg and the server
// time of its first recording.
public sealed class WorkoutSessionSet
{
    public const int MaxActualReps = RepRange.MaxReps;
    public const decimal MaxWeightKg = 1000m;
    public const int WeightDecimals = 2;

    private WorkoutSessionSet(Guid id, Guid workoutSessionExerciseId, int position, int targetMinReps, int targetMaxReps)
    {
        Id = id;
        WorkoutSessionExerciseId = workoutSessionExerciseId;
        Position = position;
        TargetMinReps = targetMinReps;
        TargetMaxReps = targetMaxReps;
    }

    public Guid Id { get; }

    public Guid WorkoutSessionExerciseId { get; }

    // 1-based set number; in a superset, the sets with the same position form a round.
    public int Position { get; }

    public int TargetMinReps { get; }

    public int TargetMaxReps { get; }

    public int? ActualReps { get; private set; }

    // Null: no external load recorded (bodyweight or unloaded). Otherwise > 0, at most two decimals.
    public decimal? WeightKg { get; private set; }

    // When the set was first recorded; a later correction keeps it.
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public bool IsCompleted => CompletedAtUtc is not null;

    internal static WorkoutSessionSet Create(WorkoutSessionExercise exercise, int position, int targetMinReps, int targetMaxReps) =>
        new(Guid.CreateVersion7(), exercise.Id, position, targetMinReps, targetMaxReps);

    // Completes a pending set, or corrects a completed one (keeping its original completion time).
    internal void Record(int actualReps, decimal? weightKg, DateTimeOffset recordedAtUtc)
    {
        Validate(actualReps, weightKg);

        ActualReps = actualReps;
        WeightKg = weightKg;
        CompletedAtUtc ??= recordedAtUtc.ToUniversalTime();
    }

    public static void Validate(int actualReps, decimal? weightKg)
    {
        if (actualReps < 1 || actualReps > MaxActualReps)
        {
            throw new ArgumentOutOfRangeException(
                nameof(actualReps),
                actualReps,
                $"Reps must be between 1 and {MaxActualReps}.");
        }

        if (weightKg is { } weight
            && (weight <= 0 || weight > MaxWeightKg || decimal.Round(weight, WeightDecimals) != weight))
        {
            throw new ArgumentOutOfRangeException(
                nameof(weightKg),
                weightKg,
                $"Weight must be more than 0 and at most {MaxWeightKg:0} kg, with at most {WeightDecimals} decimals. Leave it empty for bodyweight.");
        }
    }
}
