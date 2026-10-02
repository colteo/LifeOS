namespace LifeOS.Domain.Gym.Sessions;

public enum WorkoutSessionStatus
{
    // Started and still editable: sets can be recorded and corrected, the workout finished or discarded.
    InProgress,

    // Finished: the execution is immutable in GYM-002.
    Completed
}
