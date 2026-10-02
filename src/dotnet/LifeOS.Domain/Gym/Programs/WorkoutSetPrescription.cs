namespace LifeOS.Domain.Gym.Programs;

// One prescribed set: its order and target reps. Target weight is not part of the prescription.
public sealed class WorkoutSetPrescription
{
    private WorkoutSetPrescription(Guid id, Guid workoutBlockExerciseId, int position, int targetMinReps, int targetMaxReps)
    {
        Id = id;
        WorkoutBlockExerciseId = workoutBlockExerciseId;
        Position = position;
        TargetMinReps = targetMinReps;
        TargetMaxReps = targetMaxReps;
    }

    public Guid Id { get; }

    public Guid WorkoutBlockExerciseId { get; }

    // 1-based set number.
    public int Position { get; }

    public int TargetMinReps { get; private set; }

    public int TargetMaxReps { get; private set; }

    internal static WorkoutSetPrescription Create(WorkoutBlockExercise exercise, int position, RepRange reps) =>
        new(Guid.CreateVersion7(), exercise.Id, position, reps.TargetMinReps, reps.TargetMaxReps);

    internal void Change(RepRange reps)
    {
        TargetMinReps = reps.TargetMinReps;
        TargetMaxReps = reps.TargetMaxReps;
    }
}
