namespace LifeOS.Domain.Gym.Training;

public enum ActiveProgramStatus
{
    // Being trained: its current cycle's workouts are offered on Train.
    Active,

    // Every workout of the last cycle was completed.
    Completed,

    // Ended early by the user.
    Stopped
}
