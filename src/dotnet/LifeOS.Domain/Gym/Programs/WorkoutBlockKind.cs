namespace LifeOS.Domain.Gym.Programs;

public enum WorkoutBlockKind
{
    // One exercise: A → rest → A → rest → ...
    Single,

    // Two exercises performed back to back: A → B → rest → A → B → rest → ...
    Superset
}
