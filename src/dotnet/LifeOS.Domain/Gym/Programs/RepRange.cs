namespace LifeOS.Domain.Gym.Programs;

// The target repetitions of one prescribed set: exactly 8 (8-8) or a range such as 8-10.
public sealed record RepRange
{
    public const int MaxReps = 999;

    public RepRange(int targetMinReps, int targetMaxReps)
    {
        if (targetMinReps < 1 || targetMinReps > MaxReps)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetMinReps),
                targetMinReps,
                $"Target reps must be between 1 and {MaxReps}.");
        }

        if (targetMaxReps < targetMinReps || targetMaxReps > MaxReps)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetMaxReps),
                targetMaxReps,
                $"Maximum target reps must be at least the minimum and at most {MaxReps}.");
        }

        TargetMinReps = targetMinReps;
        TargetMaxReps = targetMaxReps;
    }

    public int TargetMinReps { get; }

    public int TargetMaxReps { get; }
}
